# Releasing BogMem.Tool

Package metadata and the default preview version live in
`Directory.Build.props`. Only the projects listed in
`eng/package-projects.txt` are release packages; currently that is the
`BogMem.Tool` .NET tool.

## Repository setup

Create a repository Actions secret named `NUGET_API_KEY`. Scope the NuGet.org
key to package push for `BogMem.Tool` and rotate it according to the NuGet.org
account policy.

BogMem is licensed under Apache 2.0. Its package carries the Apache license and
the original MemPalace MIT copyright and permission notice.

## Continuous integration

`.github/workflows/ci.yml` runs on pushes and pull requests targeting `main`.
It:

1. restores with all transitive NuGet advisories promoted to errors;
2. builds and runs the complete solution/parity suite;
3. packs the release project;
4. installs the resulting package from the artifact directory only;
5. runs an isolated lexical init/add/search lifecycle; and
6. uploads the `.nupkg` as a workflow artifact.

## Publish

Push an annotated SemVer tag to publish that exact version:

```bash
git tag -a v0.1.0-preview.1 -m "BogMem 0.1.0-preview.1"
git push origin v0.1.0-preview.1
```

The `Publish NuGet Package` workflow can also be started manually with an
explicit version. Both paths rebuild, test, pack, install, and smoke-test the
tool before pushing it to NuGet.org. Re-running the same version is safe because
the final push uses NuGet's duplicate-package guard.

Do not create or push the release tag until CI is green on the intended commit.
