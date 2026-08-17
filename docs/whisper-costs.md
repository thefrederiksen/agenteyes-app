# DevThrottle Transcription: Costs and Decisions

Date: 2026-07-07

## Decision

Recording transcription in AgentEyes runs through the signed-in DevThrottle
account using the DevThrottle-hosted `whisper-large-v3` transcription model.
AgentEyes does not expose a provider picker or a user-supplied transcription key.

## Billing model

DevThrottle owns pricing and billing server-side. AgentEyes sends the completed
recording audio to:

```
POST https://devthrottle.com/api/v1/audio/transcriptions
```

The request is authorized with the locally stored `dt_` key, and DevThrottle
draws from the user's prepaid credit balance. If the balance is empty,
DevThrottle returns HTTP 402 before running the hosted transcription.

AgentEyes records usage metadata returned by DevThrottle where available, but it
does not compute a client-side dollar estimate. The DevThrottle account and
billing pages are the source of truth for credit balance, rate card, and spend.

## Product implications

- No alternate transcription provider path exists in AgentEyes.
- Settings > Account shows DevThrottle account and credits.
- Hosted transcription pauses when credits run out.
- Add credits opens `https://devthrottle.com/account/billing`.

## 24/7 note

Historical 24/7 transcription-cost estimates in older docs assumed direct hosted
audio transcription for every second of captured audio. The current AgentEyes app
does not implement 24/7 hosted transcription. Any future always-on transcription
work must re-check the current DevThrottle rate card and should use local speech
gating before sending hosted transcription requests.
