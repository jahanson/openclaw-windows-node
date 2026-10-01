const OPEN = "[[tts:text]]";
const CLOSE = "[[/tts:text]]";
const MARKERS = [OPEN, CLOSE] as const;
const utf8 = new TextEncoder();

export type ContentSeparatorFailure =
  | "already-ended"
  | "empty-speech"
  | "incomplete-marker"
  | "invalid-unicode"
  | "missing-speech"
  | "nested-speech"
  | "repeated-speech"
  | "speech-limit"
  | "unexpected-close"
  | "unfinished-cue"
  | "unclosed-speech"
  | "written-limit";

export interface ContentSeparatorResult {
  written: string[];
  speech: string[];
  failure?: ContentSeparatorFailure;
}

export interface ContentSeparatorOptions {
  maxSpeechBytes?: number;
  maxWrittenBytes?: number;
  maxSegmentBytes?: number;
  targetSegmentBytes?: number;
  minSentenceBytes?: number;
  maxCueBytes?: number;
}

type Phase = "written" | "speech" | "after-speech" | "failed" | "ended";

/**
 * Incrementally separates one structural [[tts:text]] block from written Markdown.
 * Turn eligibility and model-attempt authority are deliberately owned by the caller.
 */
export class ContentSeparator {
  readonly #maxSpeechBytes: number;
  readonly #maxWrittenBytes: number;
  readonly #maxSegmentBytes: number;
  readonly #targetSegmentBytes: number;
  readonly #minSentenceBytes: number;
  readonly #maxCueBytes: number;

  #phase: Phase = "written";
  #failure?: ContentSeparatorFailure;
  #writtenText = "";
  #speechText = "";
  #writtenBytes = 0;
  #speechBytes = 0;
  #speechPending = "";
  #speechPendingBytes = 0;
  #cueStart = -1;
  #cueBytes = 0;
  #cueRanges: Array<{ start: number; end: number }> = [];

  #pendingCr = false;
  #pendingHighSurrogate = "";
  #markerPending = "";
  #atLineStart = true;
  #lineProbe = "";
  #lineProbeFenceChar: "`" | "~" | undefined;
  #lineProbeFenceRun = 0;
  #tickRun = "";
  #inlineTicks = 0;

  #fenceChar: "`" | "~" | undefined;
  #fenceLength = 0;
  #openingFenceRun = false;
  #fenceLineStart = false;
  #fenceClosePossible = false;
  #fenceCloseSpaces = 0;
  #fenceCloseRun = 0;
  #fenceCloseAfterRun = false;

  #writtenDelta = "";
  #speechDelta: string[] = [];

  constructor(options: ContentSeparatorOptions = {}) {
    this.#maxSpeechBytes = positive(options.maxSpeechBytes, 64 * 1024);
    this.#maxWrittenBytes = positive(options.maxWrittenBytes, 1024 * 1024);
    this.#maxSegmentBytes = positive(options.maxSegmentBytes, 4 * 1024);
    this.#targetSegmentBytes = Math.min(
      positive(options.targetSegmentBytes, 512),
      this.#maxSegmentBytes,
    );
    this.#minSentenceBytes = Math.min(
      positive(options.minSentenceBytes, 64),
      this.#targetSegmentBytes,
    );
    this.#maxCueBytes = Math.min(
      positive(options.maxCueBytes, 256),
      this.#maxSegmentBytes,
    );
  }

  get writtenText(): string {
    return this.#writtenText;
  }

  get speechText(): string {
    return this.#speechText;
  }

  get failed(): ContentSeparatorFailure | undefined {
    return this.#failure;
  }

  get hasSpeech(): boolean {
    return this.#phase === "after-speech" ||
      (this.#phase === "ended" && this.#failure === undefined);
  }

  push(chunk: string): ContentSeparatorResult {
    this.#beginCall();
    if (this.#phase === "ended") {
      this.#fail("already-ended");
      return this.#finishCall();
    }
    if (this.#phase === "failed") return this.#finishCall();

    let input = this.#pendingHighSurrogate + chunk;
    this.#pendingHighSurrogate = "";
    if (input.length > 0 && isHighSurrogate(input.charCodeAt(input.length - 1))) {
      this.#pendingHighSurrogate = input.at(-1)!;
      input = input.slice(0, -1);
    }
    for (const char of input) {
      if (char.length === 1 && isSurrogate(char.charCodeAt(0))) {
        this.#fail("invalid-unicode");
        break;
      }
      if (this.#pendingCr) {
        this.#pendingCr = false;
        this.#process("\n");
        if (char === "\n") continue;
      }
      if (char === "\r") {
        this.#pendingCr = true;
        continue;
      }
      this.#process(char);
      if (this.#failure !== undefined) break;
    }
    return this.#finishCall();
  }

  /** Commits the currently safe speech prefix for a host-controlled latency tick. */
  flush(): ContentSeparatorResult {
    this.#beginCall();
    if (this.#phase === "speech" && this.#cueStart < 0 && this.#markerPending.length === 0) {
      this.#commitSpeech(this.#speechPending.length);
    }
    return this.#finishCall();
  }

  end(): ContentSeparatorResult {
    this.#beginCall();
    if (this.#phase === "ended") {
      this.#fail("already-ended");
      return this.#finishCall();
    }
    if (this.#phase === "failed") {
      this.#phase = "ended";
      return this.#finishCall();
    }

    if (this.#pendingCr) {
      this.#pendingCr = false;
      this.#process("\n");
    }
    if (this.#pendingHighSurrogate.length > 0) this.#fail("invalid-unicode");
    this.#finishWrittenSyntax();
    if (this.#markerPending.length > 0) this.#fail("incomplete-marker");
    else if (this.#phase === "speech") this.#fail("unclosed-speech");
    else if (this.#phase === "written") this.#fail("missing-speech");

    this.#phase = "ended";
    return this.#finishCall();
  }

  #beginCall(): void {
    this.#writtenDelta = "";
    this.#speechDelta = [];
  }

  #finishCall(): ContentSeparatorResult {
    const result: ContentSeparatorResult = {
      written: this.#writtenDelta.length > 0 ? [this.#writtenDelta] : [],
      speech: this.#speechDelta,
    };
    if (this.#failure !== undefined) result.failure = this.#failure;
    return result;
  }

  #process(char: string): void {
    if (this.#phase === "failed" || this.#phase === "ended") return;
    if (this.#phase === "speech") {
      this.#processMarkerChar(char);
      return;
    }
    this.#processWrittenChar(char);
  }

  #processWrittenChar(char: string): void {
    if (this.#fenceChar !== undefined) {
      this.#processFenceChar(char);
      return;
    }
    if (this.#inlineTicks > 0) {
      this.#processInlineChar(char);
      return;
    }
    if (this.#atLineStart) {
      this.#processLineStart(char);
      return;
    }
    this.#processOrdinaryWrittenChar(char);
  }

  #processLineStart(char: string): void {
    if (this.#lineProbeFenceChar !== undefined) {
      if (char === this.#lineProbeFenceChar) {
        this.#lineProbe += char;
        this.#lineProbeFenceRun++;
        if (this.#lineProbeFenceRun === 3) {
          this.#appendWritten(this.#lineProbe);
          this.#fenceChar = this.#lineProbeFenceChar;
          this.#fenceLength = 3;
          this.#openingFenceRun = true;
          this.#atLineStart = false;
          this.#clearLineProbe();
        }
        return;
      }
      const probe = this.#lineProbe;
      this.#clearLineProbe();
      this.#atLineStart = false;
      this.#processOrdinaryWrittenText(probe);
      this.#processWrittenChar(char);
      return;
    }

    if (char === " " && this.#lineProbe.length < 3) {
      this.#lineProbe += char;
      return;
    }
    if (char === "`" || char === "~") {
      this.#lineProbe += char;
      this.#lineProbeFenceChar = char;
      this.#lineProbeFenceRun = 1;
      return;
    }

    const probe = this.#lineProbe;
    this.#clearLineProbe();
    this.#atLineStart = false;
    this.#processOrdinaryWrittenText(probe);
    this.#processOrdinaryWrittenChar(char);
  }

  #processOrdinaryWrittenText(value: string): void {
    for (const char of value) this.#processOrdinaryWrittenChar(char);
  }

  #processOrdinaryWrittenChar(char: string): void {
    if (this.#tickRun.length > 0) {
      if (char === "`") {
        this.#holdTick();
        return;
      }
      this.#finishTickRun();
      if (this.#inlineTicks > 0) {
        this.#processInlineChar(char);
        return;
      }
    }
    if (char === "`") {
      this.#holdTick();
      return;
    }
    this.#processMarkerChar(char);
    if (char === "\n" && this.#markerPending.length === 0) this.#atLineStart = true;
  }

  #processInlineChar(char: string): void {
    if (char === "`") {
      this.#holdTick();
      return;
    }
    if (this.#tickRun.length > 0) this.#finishTickRun();
    if (this.#inlineTicks === 0) {
      this.#processWrittenChar(char);
      return;
    }
    this.#appendWritten(char);
    this.#atLineStart = char === "\n";
  }

  #holdTick(): void {
    if (this.#writtenBytes + this.#tickRun.length + 1 > this.#maxWrittenBytes) {
      this.#fail("written-limit");
      return;
    }
    this.#tickRun += "`";
  }

  #finishTickRun(): void {
    if (this.#tickRun.length === 0) return;
    const length = this.#tickRun.length;
    this.#appendWritten(this.#tickRun);
    this.#tickRun = "";
    if (this.#inlineTicks === 0) this.#inlineTicks = length;
    else if (this.#inlineTicks === length) this.#inlineTicks = 0;
    this.#atLineStart = false;
  }

  #processFenceChar(char: string): void {
    this.#appendWritten(char);
    if (this.#openingFenceRun) {
      if (char === this.#fenceChar) this.#fenceLength++;
      else {
        this.#openingFenceRun = false;
        if (char === "\n") this.#resetFenceLine();
      }
      return;
    }

    if (char === "\n") {
      const closes = this.#fenceClosePossible &&
        this.#fenceCloseRun >= this.#fenceLength;
      if (closes) {
        this.#fenceChar = undefined;
        this.#fenceLength = 0;
        this.#fenceLineStart = false;
        this.#atLineStart = true;
      } else this.#resetFenceLine();
      return;
    }

    if (!this.#fenceLineStart || !this.#fenceClosePossible) return;
    if (this.#fenceCloseRun === 0) {
      if (char === " " && this.#fenceCloseSpaces < 3) {
        this.#fenceCloseSpaces++;
        return;
      }
      if (char === this.#fenceChar) {
        this.#fenceCloseRun = 1;
        return;
      }
      this.#fenceClosePossible = false;
      return;
    }
    if (!this.#fenceCloseAfterRun && char === this.#fenceChar) {
      this.#fenceCloseRun++;
      return;
    }
    if (char === " " || char === "\t") {
      this.#fenceCloseAfterRun = true;
      return;
    }
    this.#fenceClosePossible = false;
  }

  #resetFenceLine(): void {
    this.#fenceLineStart = true;
    this.#fenceClosePossible = true;
    this.#fenceCloseSpaces = 0;
    this.#fenceCloseRun = 0;
    this.#fenceCloseAfterRun = false;
  }

  #processMarkerChar(char: string): void {
    if (this.#markerPending.length === 0 && char !== "[") {
      this.#appendCurrent(char);
      return;
    }
    this.#markerPending += char;
    while (this.#markerPending.length > 0) {
      const exact = MARKERS.find((marker) => marker === this.#markerPending);
      if (exact !== undefined) {
        this.#markerPending = "";
        this.#handleMarker(exact);
        return;
      }
      if (MARKERS.some((marker) => marker.startsWith(this.#markerPending))) return;
      const literal = this.#markerPending[0]!;
      this.#markerPending = this.#markerPending.slice(1);
      this.#appendCurrent(literal);
      if (this.#phase === "failed") return;
    }
  }

  #handleMarker(marker: typeof OPEN | typeof CLOSE): void {
    if (marker === OPEN) {
      if (this.#phase === "speech") this.#fail("nested-speech");
      else if (this.#phase === "after-speech") this.#fail("repeated-speech");
      else this.#phase = "speech";
      return;
    }
    if (this.#phase !== "speech") {
      this.#fail("unexpected-close");
      return;
    }
    if (this.#cueStart >= 0) {
      this.#fail("unfinished-cue");
      return;
    }
    if (this.#speechText.trim().length === 0) {
      this.#fail("empty-speech");
      return;
    }
    this.#commitSpeech(this.#speechPending.length);
    this.#phase = "after-speech";
  }

  #appendCurrent(value: string): void {
    if (this.#phase === "speech") this.#appendSpeech(value);
    else this.#appendWritten(value);
  }

  #appendWritten(value: string): void {
    if (value.length === 0 || this.#phase === "failed") return;
    const bytes = byteLength(value);
    if (bytes > this.#maxWrittenBytes - this.#writtenBytes) {
      this.#fail("written-limit");
      return;
    }
    this.#writtenBytes += bytes;
    this.#writtenText += value;
    this.#writtenDelta += value;
  }

  #appendSpeech(value: string): void {
    if (value.length === 0 || this.#phase === "failed") return;
    const bytes = byteLength(value);
    if (bytes > this.#maxSpeechBytes - this.#speechBytes) {
      this.#fail("speech-limit");
      return;
    }
    this.#speechBytes += bytes;
    this.#speechPendingBytes += bytes;
    this.#speechText += value;
    this.#speechPending += value;

    if (this.#cueStart >= 0) {
      this.#cueBytes += bytes;
      if (value === "]") {
        if (this.#cueBytes > this.#maxCueBytes) {
          this.#fail("speech-limit");
          return;
        }
        this.#cueRanges.push({ start: this.#cueStart, end: this.#speechPending.length });
        this.#cueStart = -1;
        this.#cueBytes = 0;
      } else if (this.#cueBytes > this.#maxCueBytes) {
        this.#fail("speech-limit");
        return;
      }
    } else if (value === "[") {
      this.#cueStart = this.#speechPending.length - 1;
      this.#cueBytes = bytes;
    }

    if (this.#cueStart < 0 && /\s/u.test(value)) {
      const beforeWhitespace = this.#speechPending.slice(0, -value.length).trimEnd();
      const sentence = /[.!?;:]$/u.test(beforeWhitespace);
      if ((sentence && this.#speechPendingBytes >= this.#minSentenceBytes) ||
        this.#speechPendingBytes >= this.#targetSegmentBytes) {
        this.#commitSpeech(this.#speechPending.length);
        return;
      }
    }
    if (this.#speechPendingBytes >= this.#maxSegmentBytes) this.#commitBoundedSpeech();
  }

  #commitBoundedSpeech(): void {
    let bytes = 0;
    let index = 0;
    let lastWhitespace = 0;
    for (const char of this.#speechPending) {
      const next = bytes + byteLength(char);
      if (next > this.#maxSegmentBytes) break;
      bytes = next;
      index += char.length;
      if ((this.#cueStart < 0 || index <= this.#cueStart) &&
        !this.#cueRanges.some((range) => index > range.start && index < range.end) &&
        /\s/u.test(char)) lastWhitespace = index;
    }
    let split = lastWhitespace > 0 ? lastWhitespace :
      (this.#cueStart > 0 ? this.#cueStart : this.#cueStart === 0 ? 0 : index);
    const splitCue = this.#cueRanges.find((range) => split > range.start && split < range.end);
    if (splitCue !== undefined) split = splitCue.start;
    if (split === 0) {
      this.#fail("speech-limit");
      return;
    }
    this.#commitSpeech(split);
  }

  #commitSpeech(index: number): void {
    if (index <= 0) return;
    const segment = this.#speechPending.slice(0, index);
    if (byteLength(segment) > this.#maxSegmentBytes) {
      this.#fail("speech-limit");
      return;
    }
    this.#speechDelta.push(segment);
    this.#speechPending = this.#speechPending.slice(index);
    this.#speechPendingBytes = byteLength(this.#speechPending);
    if (this.#cueStart >= 0) this.#cueStart -= index;
    this.#cueRanges = this.#cueRanges
      .filter((range) => range.end > index)
      .map((range) => ({ start: range.start - index, end: range.end - index }));
  }

  #finishWrittenSyntax(): void {
    if (this.#phase === "speech" || this.#phase === "failed") return;
    if (this.#lineProbe.length > 0) {
      const probe = this.#lineProbe;
      this.#clearLineProbe();
      this.#atLineStart = false;
      this.#processOrdinaryWrittenText(probe);
    }
    if (this.#tickRun.length > 0) this.#finishTickRun();
  }

  #clearLineProbe(): void {
    this.#lineProbe = "";
    this.#lineProbeFenceChar = undefined;
    this.#lineProbeFenceRun = 0;
  }

  #fail(reason: ContentSeparatorFailure): void {
    if (this.#failure !== undefined) return;
    this.#failure = reason;
    this.#phase = "failed";
    this.#markerPending = "";
    this.#speechPending = "";
    this.#speechPendingBytes = 0;
    this.#speechText = "";
    this.#speechBytes = 0;
    this.#speechDelta = [];
    this.#cueStart = -1;
    this.#cueBytes = 0;
    this.#cueRanges = [];
  }
}

function byteLength(value: string): number {
  return utf8.encode(value).byteLength;
}

function positive(value: number | undefined, fallback: number): number {
  if (value === undefined) return fallback;
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new RangeError("Content separator limits must be positive safe integers.");
  }
  return value;
}

function isHighSurrogate(value: number): boolean {
  return value >= 0xd800 && value <= 0xdbff;
}

function isSurrogate(value: number): boolean {
  return value >= 0xd800 && value <= 0xdfff;
}
