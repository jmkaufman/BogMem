# Releasing BogMem packages

Package metadata and the default preview version live in
`Directory.Build.props`. Only the projects listed in
`eng/package-projects.txt` are release packages: the `BogMem.Tool` .NET tool
and the reusable `BogMem.Graph` and `BogMem.Slices` libraries. They are
versioned and published together.

## Repository setup

NuGet.org trusted publishing supplies a short-lived API key through GitHub
Actions OIDC; the repository does not store a NuGet API key. The `bojake`
NuGet.org owner has a trusted publishing policy with:

- GitHub repository owner: `bojake`
- Repository: `BogMem`
- Workflow file: `publish-nuget.yml`
- Environment: none

The publish workflow grants only `contents: read` and `id-token: write`, then
uses `NuGet/login@v1` immediately before the package push. If the repository,
workflow filename, NuGet.org owner, or optional GitHub environment changes,
update both the NuGet.org policy and the workflow together.

BogMem is licensed under Apache 2.0. Its packages carry the Apache license and
the original MemPalace MIT copyright and permission notice.

## Continuous integration

`.github/workflows/ci.yml` runs on pushes and pull requests targeting `main`.
It:

1. restores with all transitive NuGet advisories promoted to errors;
2. builds and runs the complete solution/parity suite;
3. packs all three release projects and verifies every package was produced;
4. installs the resulting tool package from the artifact directory only;
5. runs an isolated lexical init/add/search lifecycle;
6. restores a separate consumer from `BogMem.Slices` and runs an embedded
   palace lifecycle; and
7. uploads the `.nupkg` files as workflow artifacts.

## Publish

Push an annotated SemVer tag to publish that exact version:

```bash
git tag -a v0.1.0-preview.3 -m "BogMem 0.1.0-preview.3"
git push origin v0.1.0-preview.3
```

The `Publish NuGet Package` workflow can also be started manually with an
explicit version. Both paths rebuild, test, pack, install, and smoke-test the
tool and embedded package before pushing all three packages to NuGet.org.
Re-running the same version is safe because the final push uses NuGet's
duplicate-package guard.

Do not create or push the release tag until CI is green on the intended commit.
