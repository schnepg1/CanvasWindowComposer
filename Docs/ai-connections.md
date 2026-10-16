# AI connections

Open **AI Compose** in interactive overview. Choose the funding source explicitly:

| Connection | Setup | Usage |
| --- | --- | --- |
| OpenAI API key | Enter a key; enter or refresh the model list | Separately billed to the API account |
| ChatGPT plan | Continue with ChatGPT in the system browser; authorize plan usage | Eligible requests use the selected account's plan or credits |

API access remains available. An authentication error or usage limit never switches
to the other billing path. API model lists may include models without Structured
Outputs; users can enter an appropriate model ID. ChatGPT choices come from that
account's current catalog and are refreshed after account changes. No inference
request is made just by signing in or refreshing the model list.

## ChatGPT account lifecycle

- Add an account/workspace or choose a saved registration before reconnecting.
  Stable labels distinguish registrations that share an email address.
- Sign-in opens the system browser after binding an ephemeral IPv4 loopback
  listener. The app uses PKCE S256, random state and nonce, a persistent opaque
  installation ID, and the issued client ID. The exact loopback path and redirect
  URI are checked. Duplicate callback fields and mismatched returning clients fail.
- Microsoft IdentityModel validates the signed ID token against freshly fetched
  OpenAI JWKS, issuer, audience, lifetime and nonce. A returning registration must
  match its verified subject. The active registration is updated after validation.
- A successful identity-only grant is retained but cannot generate until ChatGPT
  plan usage permission is granted. The app confirms first-time plan usage once.
- Access tokens refresh near expiry, respecting the server's earliest refresh time.
  The app is single-instance; the connection service serializes renewal and saves
  access/refresh token rotation together. Invalid refresh grants require reconnecting.
- Sign-out attempts renewable-session revocation, retries transient failures once,
  then clears local tokens while retaining the account/client mapping and host ID.
  If remote revocation was not confirmed, the UI directs users to ChatGPT settings.
- Closing overview or cancelling stops pending sign-in, model discovery or generation.
  Switching connection, model or account discards an existing canvas proposal.

This integration is for the local open-source app. OpenAI controls account/model
eligibility and plan limits. Signing in does not expose ChatGPT conversations.

## Module boundaries

| Component | Owns |
| --- | --- |
| `Modules/CanvasView` | Semantic canvas data, local layout, validation, atomic Apply/Undo |
| `Modules/Ai` | Provider/connection contracts, OAuth, account lifecycle, model discovery, bounded HTTP/SSE |
| `Source/Ai` | Connection controls, system-browser launch, Windows encrypted credential storage |
| Overview composer | Explicit note context, prompt, preview rendering, apply/discard and lifecycle cancellation |

`ICanvasComposerProvider.GenerateAsync` accepts a model, request, explicit context,
progress sink and cancellation token. Providers own their credentials and return
semantic proposals without permission to mutate the canvas. Future transports can
implement this interface without changing annotation layout or storage.

The API-key adapter retains strict Structured Outputs and bounded non-streaming
responses. The ChatGPT adapter uses the public Responses endpoint with `store:false`,
`stream:true`, and no unsupported output-token cap. It reports receiving progress,
bounds wire/event/output size, and requires `response.completed` plus a completed,
valid proposal before any preview. Refusals, interrupted streams and error events
never apply partial content. Raw service errors and credentials are not shown or logged.

## Protected storage

Existing API keys remain in Windows Credential Manager. ChatGPT records are stored
outside workspace files at `%LOCALAPPDATA%\CanvasWindowComposer\AI\chatgpt.dat`.
The complete record is DPAPI-encrypted for the current Windows user and atomically
replaced. Access, refresh and ID tokens are never placed in workspace JSON, browser
storage, logs or source-controlled configuration. Corrupt or unsupported saved
state is preserved and cannot be overwritten by a new sign-in. Copying the encrypted
file to another Windows account does not transfer usable credentials.

## Verification

The solution includes portable fake-HTTP/provider/OAuth tests and Windows native
composer and DPAPI storage tests. CI runs both test projects, locked restore, build
and publish. OAuth tests use locally signed synthetic identities; no real account,
API key or paid inference is used in tests.

Manual Windows checks before release:

1. Sign in with an eligible real account, consent to plan usage, choose a model and
   generate a preview; Apply then Undo the complete result.
2. Restart, refresh models, reconnect the same account and add another workspace.
   Confirm distinct labels, correct funding text, and model/proposal invalidation.
3. Cancel browser sign-in and generation; verify no late preview or canvas edit.
4. Check a declined plan grant, a usage-limit response, token expiry and sign-out
   with networking unavailable. Confirm API billing is never selected automatically.
5. Inspect panel layout and browser return on a real mixed-DPI Windows desktop.

## Official references checked 16 October 2026

- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations
- https://developers.openai.com/siwc/ui-ux-guidelines
- https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens/8.23.0
