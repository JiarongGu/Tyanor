# Every Stop Is Classified — Credentials, Transient, or Hard

**A provider error is never merely "it failed". It is classified into one of three classes, and each earns
a different response: credentials → PAUSE (resumable), transient → retry bounded then PAUSE, hard → FAIL.
Only `FailureClass.Hard` is terminal.**

## Why

An expired token and a malformed definition both end a run, but only one of them means the work already
done was wasted. A tool that hard-fails a credential error throws away twenty minutes of correct
provisioning, tells the operator nothing they can act on, and teaches them to fear running it again.

The three classes are not a taxonomy for its own sake — they are three *different things the operator
should do next*: re-authenticate, wait, or change the definition. If a fourth class ever earns its place,
it must arrive with a fourth action.

Retrying is also a claim. Retrying a transient blip is honest; retrying a malformed request is a lie told
five times, and retrying an expired credential merely delays the moment someone can fix it.

## How to Apply

- **Providers classify; the engine never inspects an exception it did not create.** Implement
  `IFailureClassifier` next to the driver. Returning `null` means "not mine", and the engine treats that as
  `Hard` — the safe default, because an unrecognised error is exactly the one you must not silently retry.
- **Walk the whole `InnerException` chain.** Providers routinely wrap the informative exception inside a
  generic one; a classifier reading only the outermost will call an expired token a hard failure. This is
  the most common way a classifier goes quietly wrong.
- **Classify on codes, not messages.** Message text is not API surface and changes without notice.
- **Retry only `Transient`** (`ProcedureRunner.WithRetryAsync`). Credentials and hard failures rethrow at
  once.
- **Unit-test the classifier directly.** A mocked SDK cannot catch a wrong or missing status code — the
  test has to name the real codes. This is the part of a provider most worth testing without a cloud.
- **A pause says the work is kept, and means it.** The operator-facing wording for a pause must say so;
  `ProcedureRunner.Explain` is the reference.

## Edge cases

- **Account-level gates** (a quota, an unverified account, a region not enabled) are `Hard` even though
  they feel transient — no amount of retrying resolves them, and looping hides the one message that would.
- **A 5xx that never stops** is still `Transient`: it pauses after the retry budget rather than failing,
  because nothing about the desired state is wrong.

## Related

- [`reconcile-dont-mirror.md`](reconcile-dont-mirror.md) · `docs/DECISIONS.md` D2
