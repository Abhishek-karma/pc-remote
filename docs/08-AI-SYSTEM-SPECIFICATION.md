# AI System Specification

## 1. Current Status: No AI/ML Component (v1)

This project does not ship any AI, machine learning, or LLM-based feature in
v1. Both the Windows agent and Android app are deterministic, rule-based
systems: gestures map to fixed input-simulation calls, and there is no
model inference, personalization engine, or generative content anywhere in
the current architecture (`05-TECHNICAL-ARCHITECTURE.md`).

This document exists as a placeholder in the standard doc set and to record
AI-adjacent ideas that have been considered so they aren't silently lost or
mistaken for planned/implemented work.

## 2. Explicitly Rejected or Deferred AI Ideas

| Idea | Why not in v1 |
|---|---|
| Voice control ("Hey PC, pause") | Requires on-device or cloud speech recognition, microphone permission, and wake-word handling — significant scope beyond a touch-based remote; also raises privacy questions (always-listening mic) that conflict with the project's local-only, minimal-permissions stance (`09-SECURITY-PRIVACY.md`) |
| Predictive/adaptive touchpad sensitivity (learning a user's gesture speed over time) | Adds a stateful personalization layer for a marginal UX gain over a simple manual sensitivity slider (`02-FEATURE-SPECIFICATION.md` F2) |
| Smart macro suggestions ("you often do X then Y, want a shortcut?") | Depends on usage-pattern tracking/telemetry, which this project deliberately avoids by default (`14-OBSERVABILITY-LOGGING.md`) |
| On-device gesture recognition beyond basic tap/drag/long-press | Current gesture set is simple enough that standard Compose gesture detectors suffice; no ML model needed |

## 3. If AI Features Are Added Later

Should a future version add an AI-based feature (most plausibly voice
control, given it's the most-requested "smart remote" pattern), the
following would need to be specified here before implementation:

- **Where inference runs:** on-device (e.g., Android's built-in speech
  recognizer) vs. cloud API — this materially changes the privacy posture
  documented in `09-SECURITY-PRIVACY.md` §5, since cloud inference would be
  the first time any user data leaves the local network.
- **Data handling:** what audio/text is captured, whether it's retained,
  and explicit user consent/opt-in flow — this project's current stance
  (`09-SECURITY-PRIVACY.md`) is that *no* data leaves the device pair, so
  any cloud-dependent AI feature would be a notable policy exception
  requiring clear disclosure.
- **Fallback behavior:** what happens when recognition fails or is
  unavailable offline — should never block the existing manual controls.
- **Model/vendor choice, latency budget, and cost** (if cloud-based).

Until a concrete AI feature is scoped and approved, this section remains
non-normative — nothing here should be treated as a commitment or roadmap
item.
