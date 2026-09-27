# Deployment prerequisites

The documentation can build successfully while GitHub Pages deployment fails because no Pages site exists. The workflow deliberately does not disguise this as a successful deployment.

## GitHub Pages

A repository administrator must set **Settings → Pages → Build and deployment → Source → GitHub Actions**, then rerun the Documentation workflow on main. Its intended address is `https://wieslawsoltes.github.io/ProPDF/`.

The built-in `GITHUB_TOKEN` used by the deployment workflow does not grant repository-administration rights to provision a missing site. `actions/configure-pages` automatic enablement requires an appropriately scoped separate token; no such token is stored in this repository. Do not add a personal access token to source or silently substitute an unrelated hosting service.

A successful strict MkDocs build and downloadable site artifact are distinct from an active public Pages deployment. Check the deploy job's actual result.

## NuGet and GitHub releases

Configure ownership of the seven package IDs and a scoped `NUGET_API_KEY` in the protected `nuget-release` environment. Set required reviewers and appropriate branch/tag restrictions. Review all dependency licensing, especially iText/pdfSweep, before distributing compiled samples.

The Release workflow supports a nonpublishing dry run and requires an exact immutable version tag reachable from main for publication. Library artifacts from ordinary CI are usable as a local NuGet feed but are not a public NuGet listing. No secret, signing credential or commercial license entitlement is included in source.

See [Build and release](build-release.md) for validation, archive contents and partial-publication recovery.

Primary references: [GitHub Pages configuration](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site), [configure-pages](https://github.com/actions/configure-pages).
