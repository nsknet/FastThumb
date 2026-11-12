# Contributing to FastThumb

Thank you for considering a contribution. Bug reports, new hostile-input test cases, fidelity improvements and
documentation fixes are all welcome.

- **Bugs and feature requests**: open an issue at <https://github.com/nsknet/FastThumb/issues>.
- **Security problems**: do not open an issue; follow [SECURITY.md](SECURITY.md).
- **Larger changes** (a new format, a new option, a new native dependency): please open an issue first to discuss
  the approach.

## Repository layout

| Path | Contents |
|---|---|
| `src/FastThumb/` | The library. `Api/` is the public surface; `Words/`, `Cells/`, `Slides/`, `Fixed/` are the format readers; `Layout/` and `Scene/` do layout, rasterising and PDF writing; `Video/` is the interop with thumbvideo. |
| `src/FastThumb.Cli/` | The `fastthumb` command-line tool. |
| `tests/FastThumb.Tests/` | xUnit tests. `Fixtures/` generates every test input in code. |
| `tests/fixtures/` | The few binary fixtures (tiny generated videos) and the script that creates them. |
| `native/thumbvideo/` | The native video library: C shim and build script. |
| `eng/` | Scripts used by CI: placing native artifacts, checking the package layout, smoke-testing the packages. |
| `docs/` | Documentation. Start with [docs/architecture.md](docs/architecture.md). |

## Building

Requirements: the .NET 10 SDK (the exact band is in [global.json](global.json)) and, to run the `net8.0` tests, the
.NET 8 runtime.

```bash
dotnet build FastThumb.slnx -c Release
```

Warnings are errors. The library targets `net8.0` and `net10.0`; the command-line tool targets `net8.0` and rolls
forward.

On Linux, install fontconfig and the fonts listed in [docs/linux.md](docs/linux.md) before running tests.

## Testing

```bash
dotnet test FastThumb.slnx -c Release
```

- Tests run on both target frameworks.
- Test inputs are **generated in code** (`tests/FastThumb.Tests/Fixtures`): office documents are assembled from
  their XML parts, PDFs and images are drawn, hostile files are constructed byte by byte. Add new inputs the same
  way.
- The video tests need the thumbvideo library for your platform (next section); they are skipped, not failed, when
  it is missing.
- A change to a reader should come with a test that exercises it. A fix for a crash, hang or resource problem should
  come with a hostile-input test that fails without the fix.

Try the command-line tool against your own files:

```bash
dotnet run --project src/FastThumb.Cli -c Release -- run path/to/files -o out -v
dotnet run --project src/FastThumb.Cli -c Release -- info
```

## Native build (thumbvideo)

The thumbvideo binaries are not committed (`src/FastThumb/runtimes/` is git-ignored). CI builds them for all six
runtime identifiers. Locally, build the one for your platform:

```bash
sh native/thumbvideo/build.sh linux-x64     # or win-x64, win-arm64, linux-arm64, osx-x64, osx-arm64
```

This needs zig, make, git, meson, ninja and, for the x64 targets, nasm. Details are in
[docs/native.md](docs/native.md) and [native/thumbvideo/README.md](native/thumbvideo/README.md). Without the library
everything except video works (a video input fails with `UnsupportedFormatException`) and the video tests are skipped.

If you change anything in `native/thumbvideo/`, build at least your own platform and run the tests; CI builds and
tests the others.

## Code style

- Formatting is defined in [.editorconfig](.editorconfig) and checked in CI:

  ```bash
  dotnet format FastThumb.slnx --verify-no-changes --severity warn
  ```

  Run `dotnet format FastThumb.slnx` before committing. Lines are at most 160 characters; namespaces are
  file-scoped; files use LF line endings.
- Nullable reference types are enabled and warnings are errors.
- Public API changes need XML documentation (the build generates the documentation file) and an entry in
  [CHANGELOG.md](CHANGELOG.md) and, where relevant, in [docs/options.md](docs/options.md).
- Comments explain *why*, not *what*, and are written in English.
- Keep dependencies minimal. A new NuGet or native dependency needs a discussion first, a pinned version in
  `Directory.Packages.props`, and an entry in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

### Rules for code that reads input

Every input is untrusted (see [docs/security.md](docs/security.md)). In readers and layout code:

- Never trust a size, count, offset or index from the file: check it against the data actually present before
  allocating or looping.
- Read ZIP entries, OLE streams and decompression streams through `Guard.ReadAll`, `Guard.Open` or `Guard.Limit`,
  never directly.
- Parse XML through `Guard.LoadXml`, `Guard.ParseXml` or `Guard.Depth` with `Guard.Xml` settings (no DTD).
- Call `Guard.Deeper()` at the top of every recursive function.
- Decode embedded pictures through `Guard.Image`.
- Report unsupported input with `NotSupportedException` and damaged input with `InvalidDataException` (they are
  translated to the public `UnsupportedFormatException` and `InvalidDocumentException`); report an exceeded limit
  with `ResourceLimitExceededException`.
- Never follow a reference out of the file: no file paths, no URLs, no network.
- Use `RegexOptions.NonBacktracking` for regular expressions applied to input.
- Do not add process-wide caches keyed by values from documents without a size bound.

## Sample files

**Never commit third-party sample files**: no documents, images or videos taken from customers, colleagues, the web
or other projects, however useful they are for reproducing a problem. Such files usually carry copyright or personal
data, and they cannot be removed from the git history afterwards.

Instead:

- Reproduce the problem with a generated input in `tests/FastThumb.Tests/Fixtures`.
- If a binary fixture is unavoidable, generate it from scratch with a script that is committed next to it (as
  `tests/fixtures/make-videos.sh` does for the videos), and keep it tiny.
- In an issue, describe the structure that triggers the problem rather than attaching a confidential file.

Do not commit keys, certificates or license files either.

## Pull request checklist

- [ ] The change is focused; unrelated refactoring is left out.
- [ ] `dotnet build FastThumb.slnx -c Release` succeeds without warnings.
- [ ] `dotnet test FastThumb.slnx -c Release` passes.
- [ ] `dotnet format FastThumb.slnx --verify-no-changes --severity warn` reports nothing.
- [ ] New behaviour and fixed bugs are covered by tests with generated inputs.
- [ ] Code that reads input follows the rules above.
- [ ] No third-party sample files, keys or license files are added.
- [ ] Public API changes are documented (XML comments, `docs/`, `CHANGELOG.md`).
- [ ] New or updated dependencies are reflected in `THIRD-PARTY-NOTICES.md`.

## Releases

Maintainers release by pushing a tag `vX.Y.Z`. The release workflow runs the full CI, packs `FastThumb` with the
version from the tag (MinVer), publishes it to nuget.org and creates a GitHub release.

## License

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE) of the project.
