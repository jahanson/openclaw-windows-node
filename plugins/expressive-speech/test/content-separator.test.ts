import assert from "node:assert/strict";
import test from "node:test";

import { ContentSeparator } from "../src/content-separator.ts";

const OPEN = "[[tts:text]]";
const CLOSE = "[[/tts:text]]";
const SOURCE = `Visible command: \`Get-Process\`.\r\n${OPEN}[curious] Spoken first.\rSecond sentence.${CLOSE}\r\nDone.`;
const WRITTEN = "Visible command: `Get-Process`.\n\nDone.";
const SPEECH = "[curious] Spoken first.\nSecond sentence.";

test("separates one block at every possible input boundary and normalizes line endings", () => {
  for (let split = 0; split <= SOURCE.length; split++) {
    const separator = new ContentSeparator();
    const output = [separator.push(SOURCE.slice(0, split)), separator.push(SOURCE.slice(split)), separator.end()];
    assert.equal(output.flatMap((value) => value.written).join(""), WRITTEN, `written split ${split}`);
    assert.equal(output.flatMap((value) => value.speech).join(""), SPEECH, `speech split ${split}`);
    assert.equal(separator.writtenText, WRITTEN, `written getter split ${split}`);
    assert.equal(separator.speechText, SPEECH, `speech getter split ${split}`);
    assert.equal(separator.hasSpeech, true, `speech state split ${split}`);
    assert.equal(separator.failed, undefined, `failure split ${split}`);
  }
});

test("preserves literal markers in fenced and inline Markdown code", () => {
  const source = [
    "Literal inline `[[tts:text]]not speech[[/tts:text]]`.\n",
    "```text\n[[tts:text]]also literal[[/tts:text]]\n```\n",
    `${OPEN}[warmly] Actual speech.${CLOSE}\nWritten tail.`,
  ].join("");
  const expectedWritten = [
    "Literal inline `[[tts:text]]not speech[[/tts:text]]`.\n",
    "```text\n[[tts:text]]also literal[[/tts:text]]\n```\n",
    "\nWritten tail.",
  ].join("");
  for (let split = 0; split <= source.length; split++) {
    const separator = new ContentSeparator();
    const output = [separator.push(source.slice(0, split)), separator.push(source.slice(split)), separator.end()];
    assert.equal(output.flatMap((value) => value.written).join(""), expectedWritten, `written split ${split}`);
    assert.equal(output.flatMap((value) => value.speech).join(""), "[warmly] Actual speech.", `speech split ${split}`);
  }
});

test("explicit flush commits speech without waiting for the complete response", () => {
  const separator = new ContentSeparator();
  const first = separator.push(`${OPEN}[curious] A useful early phrase`);
  assert.deepEqual(first.speech, []);
  const tick = separator.flush();
  assert.deepEqual(tick.speech, ["[curious] A useful early phrase"]);
  const last = separator.push(` continues.${CLOSE}Written.`);
  assert.deepEqual(last.speech, [" continues."]);
  assert.equal(separator.end().failure, undefined);
  assert.equal(separator.speechText, "[curious] A useful early phrase continues.");
});

test("keeps cue tokens whole while bounding segment byte size", () => {
  const separator = new ContentSeparator({ targetSegmentBytes: 24, maxSegmentBytes: 40, minSentenceBytes: 8 });
  const outputs = [
    separator.push(`${OPEN}[very curious] One short sentence. `),
    separator.push("Another useful sentence. "),
    separator.push(`Final.${CLOSE}`),
    separator.end(),
  ];
  const segments = outputs.flatMap((value) => value.speech);
  assert.equal(segments.join(""), "[very curious] One short sentence. Another useful sentence. Final.");
  assert.ok(segments.every((segment) => new TextEncoder().encode(segment).byteLength <= 40));
  assert.ok(segments.some((segment) => segment.includes("[very curious]")));
  assert.ok(segments.every((segment) => !segment.includes("[very") || segment.includes("[very curious]")));

  const tight = new ContentSeparator({ maxSegmentBytes: 10, targetSegmentBytes: 10, minSentenceBytes: 10 });
  const tightOutput = [tight.push(`${OPEN}abc[123456]z${CLOSE}`), tight.end()];
  const tightSegments = tightOutput.flatMap((value) => value.speech);
  assert.equal(tightSegments.join(""), "abc[123456]z");
  assert.ok(tightSegments.some((segment) => segment.includes("[123456]")));
  assert.ok(tightSegments.every((segment) => !segment.includes("[") || segment.includes("[123456]")));
  assert.ok(tightSegments.every((segment) => new TextEncoder().encode(segment).byteLength <= 10));
});

test("fails closed for malformed structures while retaining prior written content", () => {
  const cases = [
    { source: `Before.${CLOSE} leaked`, failure: "unexpected-close", written: "Before." },
    { source: `Before.${OPEN}hidden`, failure: "unclosed-speech", written: "Before." },
    { source: `Before.${OPEN}one ${OPEN}two${CLOSE}`, failure: "nested-speech", written: "Before." },
    { source: `Before.${OPEN}one${CLOSE} middle ${OPEN}two${CLOSE}`, failure: "repeated-speech", written: "Before. middle " },
    { source: "Before.[[tts:te", failure: "incomplete-marker", written: "Before." },
  ] as const;
  for (const fixture of cases) {
    const separator = new ContentSeparator();
    const output = [separator.push(fixture.source), separator.end()];
    assert.equal(separator.failed, fixture.failure, fixture.failure);
    assert.equal(output.flatMap((value) => value.written).join(""), fixture.written, fixture.failure);
    assert.equal(separator.speechText, "", fixture.failure);
    assert.equal(separator.hasSpeech, false, fixture.failure);
  }
});

test("missing and empty speech are explicit failures without hiding valid written text", () => {
  const missing = new ContentSeparator();
  const missingOutput = [missing.push("Written only."), missing.end()];
  assert.equal(missing.failed, "missing-speech");
  assert.equal(missingOutput.flatMap((value) => value.written).join(""), "Written only.");

  const empty = new ContentSeparator();
  const emptyOutput = [empty.push(`Written.${OPEN} \n ${CLOSE}tail`), empty.end()];
  assert.equal(empty.failed, "empty-speech");
  assert.equal(emptyOutput.flatMap((value) => value.written).join(""), "Written.");
});

test("enforces written, speech, segment and cue bounds", () => {
  const written = new ContentSeparator({ maxWrittenBytes: 4 });
  assert.equal(written.push("12345").failure, "written-limit");

  const speech = new ContentSeparator({ maxSpeechBytes: 4 });
  assert.equal(speech.push(`${OPEN}12345`).failure, "speech-limit");

  const cue = new ContentSeparator({ maxCueBytes: 8, maxSegmentBytes: 16 });
  assert.equal(cue.push(`${OPEN}[1234567]`).failure, "speech-limit");

  const heldTicks = new ContentSeparator({ maxWrittenBytes: 8 });
  assert.equal(heldTicks.push("x````````").failure, "written-limit");
});

test("does not split multibyte characters when enforcing segment bytes", () => {
  const separator = new ContentSeparator({ targetSegmentBytes: 16, maxSegmentBytes: 16, minSentenceBytes: 16 });
  const output = [separator.push(`${OPEN}🙂🙂🙂🙂🙂${CLOSE}`), separator.end()];
  const segments = output.flatMap((value) => value.speech);
  assert.equal(segments.join(""), "🙂🙂🙂🙂🙂");
  assert.ok(segments.every((segment) => new TextEncoder().encode(segment).byteLength <= 16));
  assert.ok(segments.every((segment) => !segment.includes("�")));
});

test("carries a surrogate pair across chunks and rejects malformed Unicode", () => {
  const pair = "🙂";
  const separator = new ContentSeparator({ maxSpeechBytes: 4, maxSegmentBytes: 4 });
  assert.equal(separator.push(`${OPEN}${pair[0]}`).failure, undefined);
  assert.equal(separator.push(`${pair[1]}${CLOSE}`).failure, undefined);
  assert.deepEqual(separator.end().speech, []);
  assert.equal(separator.speechText, pair);

  const malformed = new ContentSeparator();
  malformed.push(OPEN);
  assert.equal(malformed.push("\ud800x").failure, "invalid-unicode");
});
