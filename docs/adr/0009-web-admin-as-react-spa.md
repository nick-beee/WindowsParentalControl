# 9. Web Admin as a React Single-Page App

## Status
Accepted (2026-11-26). Not implemented yet: issue #26.

## Context
1. **UI inside C# strings**: The web admin (`Web/Pages/AdminPage.cs`, 1712 lines) and the child request page (`Web/Pages/RequestPage.cs`, 960 lines) are C# raw strings. They hold the HTML, the CSS and 59 JavaScript functions. No editor, type check, linter or test reads this code.
2. **Manual escaping**: The page builds HTML with template literals and `innerHTML`. Each value needs a manual `esc()` call. The fix for the stored XSS in #5 (PR #23) had to correct about 30 places, and each new table can add the same bug again.
3. **No Content Security Policy**: The page uses inline scripts and inline `onclick` handlers. So a strict CSP is not possible (#25).
4. **Merge conflicts**: Most changes touch the same file. On 2026-11-26, three changes (#18, #11, #23) changed `AdminPage.cs` at the same time.
5. **The API exists**: The service already has a JSON API for the admin (`/api/admin/*`). The page uses only this API after the first load.
6. **Stage 4**: The roadmap can move the panel to the home server. A UI that talks only to an API can move without a change.

## Decision
1. **React + TypeScript + Vite**: The web admin becomes a single-page app in a new folder `web/admin`. It has one typed API client for `/api/admin/*`. React escapes text by default; code that sets raw HTML needs a review.
2. **Served by the service**: The Vite build writes static files. The service project embeds them as resources and serves them at `/admin` from Kestrel. No second server and no second port. The JSON API, the session cookie and the sign-in rules of #18 stay as they are.
3. **One build command**: An MSBuild target in `ParentalControl.Service.csproj` runs `npm ci` and `npm run build` before the build, if the bundle is missing or older than its sources. A property can skip it. CI and `build.ps1` get a Node setup step. The installer does not change.
4. **Strict CSP**: `/admin` sends `Content-Security-Policy` with `script-src 'self'` and no inline scripts.
5. **Tests**: CI runs the type check, the linter and component tests (Vitest).
6. **Order**: First the admin page, with the same features as today. Then the child request page. No behaviour change, so the move is a refactor PR without an OpenSpec change. It starts after #18 and #11 are merged, so their changes go into the new UI and nobody does the work two times.

**Rejected alternatives:**
- *Static HTML and JavaScript files without a framework*: gives editor support and a CSP, but the escaping stays manual.
- *Blazor*: the UI stays in C#, but Blazor WebAssembly makes a large download, and Blazor Server needs a SignalR connection for each page.
- *Keep the C# strings*: each new feature adds more manual escaping and more conflicts.

## Consequences
- The XSS bug class of #5 mostly goes away, and a strict CSP becomes possible.
- The UI gets types, a linter, tests and editor support.
- The build needs Node (Node 24 on the Mac, `actions/setup-node` in CI). Test VM 102 has no Node: it needs Node, or the Mac builds the bundle before `scripts/vm-test.sh` copies the tree.
- The repo gets a `package.json` and a lock file. Their dependencies need updates like the NuGet packages.
- The rewrite of about 1700 lines of UI is one large change. During it, other changes of the admin page must wait or go into the new UI.
