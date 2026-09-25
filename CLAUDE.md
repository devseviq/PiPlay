# PiPlay repository rules

Read [`docs/AGENTS.md`](docs/AGENTS.md), [`docs/PiPlay_Product_Engineering_Spec.md`](docs/PiPlay_Product_Engineering_Spec.md), and [`docs/DECISIONS.md`](docs/DECISIONS.md) before changing code. Keep behavior claims tied to source/tests; do not add status prose or approval steps.

## Verification

```powershell
pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1
```

For a deployed Stable check, set `PIPLAY_STABLE_ROOT` and pass it to `Publish-Stable.ps1`, `Verify-StableDeploy.ps1`, and `Test-UiSmoke.ps1`. Never use source or `bin` output as manual/release evidence. The user checks the final deployed result; scripts own intermediate verification.

## Release stamps

`VERSION` is semantic version; `BUILD_NUMBER` is the publish counter. Exact-source Stable publishing requires committed stamps and a clean tree, creates `stable-vX.Y.Z-bN`, and verifies the deployed manifest before manual acceptance. `-AllowVersionBump`, `-AllowDirty`, and `-SkipTests` are diagnostic-only, never release evidence, and none of them creates a stable tag. Optional signing runs through `-SignScript` before manifest hashes.
