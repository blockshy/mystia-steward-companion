# Project Guidelines

## Basic Principles

- Long-lived repository knowledge belongs in `docs/`.
- Keep user-facing documentation in Chinese unless a feature explicitly requires English.
- Preserve unrelated changes and keep modifications scoped to the current task.
- When architecture, rules, or build behavior changes, update the matching README or `docs/` file.

## Code Style

- Use strict TypeScript and avoid `any` unless there is no practical typed alternative.
- Use function components and hooks for React code.
- Import `src` modules through the `@/` alias.
- Prefer existing primitives in `apps/companion/src/components/ui` before adding new controls.
- Do not hard-code balancing values in components; update `apps/companion/src/data` and consume it through typed logic.

## Architecture

- This repository only maintains the BepInEx Mod and Tauri companion window.
- Companion frontend entry: `apps/companion/src/companion/ModWorkbench.tsx`.
- Recommendation logic:
  - Normal customers: `apps/companion/src/lib/normal-recommend.ts`
  - Rare customers: `apps/companion/src/lib/rare-recommend.ts`
  - Tag conflict and scoring rules: `apps/companion/src/lib/tags.ts`
- Structured game data lives in `apps/companion/src/data/*.json` and is synchronized into `mods/mystia-steward-bepinex/Data/`.
- C# Mod code must not import TypeScript modules. Shared data crosses the boundary as JSON only.
- The Mod reads live runtime data from the game. Do not reintroduce `.memory` save import pages.

## Build And Validation

- Install dependencies: `pnpm install`
- Companion frontend build: `pnpm build`
- Lint: `pnpm lint`
- Tauri build: `pnpm tauri:build`
- BepInEx plugin build: `dotnet build mods/mystia-steward-bepinex/MystiaSteward.BepInEx.csproj -c Release`
- Release package: `powershell -ExecutionPolicy Bypass -File mods\mystia-steward-bepinex\tools\build-release.ps1`

## Key Reference Files

- `README.md`: repository overview and build flow.
- `mods/mystia-steward-bepinex/README.md`: user installation and usage.
- `mods/mystia-steward-bepinex/README.dev.md`: developer setup and troubleshooting.
- `mods/mystia-steward-bepinex/docs/RUNTIME_PROVIDER_NOTES.md`: runtime reflection details.
- `docs/tmi-cooking-mechanics-knowledge-base.md`: cooking, tags, and scoring rules.
