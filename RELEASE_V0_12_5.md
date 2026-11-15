# Release V0.12.5 Beta

Manual one-time publication request for the existing, validated Dalamud plugin build.

- Version: `0.12.5.0`
- GitHub Release tag: `v0.12.5-beta`
- Source: `main`
- Windows CI already compiled successfully
- The optional Connect account deletion remains server-gated until production backend and DB schema pass review.
- This trigger path is dedicated to V0.12.5 and does not publish on normal source pushes.

Keep the Server API production rollout independent; do not deploy DB-changing code automatically.
