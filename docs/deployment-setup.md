# Deployment prerequisites

Build artifacts, successful validation and an active public deployment are separate outcomes. Check the exact commit and deploy job before claiming publication.

## GitHub Pages

The **Uno browser and Pages** workflow publishes one payload: the actual Uno PDF editor at `/ProPDF/`, documentation at `/ProPDF/docs/`, and dependency notices at `/ProPDF/licenses/`. The Documentation workflow only validates its site and no longer overwrites the editor deployment.

When no Pages site exists, a repository administrator must set **Settings → Pages → Build and deployment → Source → GitHub Actions**, then rerun **Uno browser and Pages** on main. Its intended address is `https://wieslawsoltes.github.io/ProPDF/`.

The workflow requests ordinary Pages-write and OIDC deployment permissions. Automatic initial enablement can require repository-administration rights beyond `GITHUB_TOKEN`; the workflow does not invent credentials or substitute an unrelated hosting service. Do not commit a personal access token to source. A successful application artifact is not evidence of a live URL.

## NuGet and GitHub releases

Configure ownership of the nine package IDs and a scoped `NUGET_API_KEY` in the protected `nuget-release` environment. Set required reviewers and appropriate branch/tag restrictions. Preserve dependency licensing and native notices when distributing compiled samples.

Release supports a nonpublishing dry run and requires an exact immutable version tag reachable from main for publication. Ordinary CI packages are usable as a local feed, not proof of a public NuGet listing. No NuGet secret or signing credential is included in source.

See [Build and release](build-release.md) for checks and failure recovery, and [Uno Platform](uno-platform.md) for the actual browser/native target boundaries.

Primary references: [Pages publishing configuration](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site), [configure-pages](https://github.com/actions/configure-pages).
