# Contributing to Ground Control (RTS)

Thanks for wanting to help. This is a hobby project with very little maintainer time, so the rules
below are strict on purpose. Please read them before opening anything.

## The short version

1. **Every pull request must come from an issue.** No issue, no PR.
2. **Every pull request must say clearly WHY and HOW.**
3. **Obviously bad, vibe coded code is rejected without feedback.**
4. **A pull request can be closed without a review.** This saves the maintainers' time.

## Issues first

- Open an issue (or pick an existing one) before you write code, and wait for a maintainer to say
  the change is wanted. A PR for something nobody agreed to is likely to be closed.
- Bug reports need: the mod version, the mission, singleplayer or multiplayer (host or guest), what
  you did, what you expected, what happened, and the BepInEx log (`BepInEx/LogOutput.log`).
- Feature requests need: what you want to do in the game and why the mod cannot do it today.
- One problem per issue.

## Pull requests

Link the issue in the description (`Fixes #123`). The description must answer two questions:

- **WHY:** what problem this solves, and what the root cause was if it is a bug.
- **HOW:** what you changed, why it is the right place to change it, and how you tested it in the
  game.

A PR without both is closed.

Also expected:

- One issue per PR. Keep the diff small and on topic. No drive-by refactors, reformatting or renames.
- It builds with no warnings and passes the lint workflow.
- You ran it in the game. Say what you tested. "It compiles" is not a test.
- User-visible changes are added to `CHANGELOG.md` under `## Unreleased` and documented in
  `README.md`.
- Do not bump the version and do not commit the built DLL. The maintainers do that on release.

## Code quality

You may use AI tools. You may not hand in code you do not understand. A PR is rejected without
feedback when it shows signs like these:

- The author cannot explain what the change does or why it is there.
- Large diffs, unrelated rewrites, or duplicates of code that already exists in the repo.
- Dead code, empty abstractions, or comments that repeat what the next line does.
- Never built, never run in the game.

## Rejection without review

Maintainers may close any issue or pull request at any time, without a review and without an
explanation. That is not personal. There are few maintainers and the time goes to the mod first.
If your PR was closed and you believe it meets everything above, say so in the linked issue, once.

## Building

See [Building from source](README.md#building-from-source).

## License

By contributing you agree that your contribution is licensed under the project's
[LICENSE](LICENSE), and that the project owner may also relicense it under other terms later
(condition 7 of the LICENSE).
