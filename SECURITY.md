# Security policy

FastThumb reads files that may come from untrusted sources, so security reports are taken seriously. The design,
the resource limits and the recommended deployment are described in [docs/security.md](docs/security.md).

## Supported versions

Security fixes are released for the latest minor version of the latest major version.

| Version | Supported |
|---|---|
| 1.x (latest minor) | Yes |
| Older versions | No |

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Report it privately through GitHub: open <https://github.com/nsknet/FastThumb/security/advisories/new> (the
"Report a vulnerability" button on the repository's **Security** tab). Include:

- the FastThumb version, the .NET version and the platform (runtime identifier);
- what happens (crash, hang, memory or CPU exhaustion, out-of-bounds access, information disclosure, ...);
- a minimal input file that reproduces it, or the steps to create one;
- the options used, if they differ from `ThumbnailOptions.Default`.

Do not include files that contain personal or confidential data; a reduced or synthetic file is preferred.

## What to expect

- An acknowledgement within 5 working days.
- An assessment and, for a confirmed vulnerability, a plan for the fix within 30 days.
- A fixed release and a published advisory that credits the reporter, unless you prefer to stay anonymous.

Please give the maintainers a reasonable time to release a fix before disclosing details publicly.

## Scope

In scope:

- Any input that makes FastThumb crash the process, hang, or use memory, CPU or disk beyond its documented limits.
- Any input that makes FastThumb read files, reach the network or execute code.
- Memory-safety problems in `thumbvideo` (the C code in [native/thumbvideo](native/thumbvideo)).
- Problems in the `fastthumb` command-line tool, the Dockerfile or the build and release workflows.

Vulnerabilities in third-party components (FFmpeg, dav1d, PDFium, libvips and its codecs, Skia) should be reported
to their own projects. Do tell us as well when FastThumb ships an affected version, so that the dependency can be
updated; an input that reaches such a bug through FastThumb is in scope for a FastThumb update.

Out of scope:

- A slow but bounded rendering of a large, valid document.
- Differences in rendering fidelity.
