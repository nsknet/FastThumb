# FastThumb command-line tool in a container.
#
#   docker build -t fastthumb .                                     (from the repository root)
#   docker build --build-arg CJK_FONTS=true -t fastthumb .          (+ Chinese/Japanese/Korean fonts, ~100 MB)
#   docker run --rm fastthumb info
#   docker run --rm -v "$PWD/in:/in:ro" -v "$PWD/out:/out" fastthumb run --out /out /in
#
# The native libraries come with the NuGet packages (Skia, libvips, PDFium) and with src/FastThumb/runtimes (thumbvideo,
# built by native/thumbvideo/build.sh or by CI). All are glibc builds: the base image must be Debian/Ubuntu, not Alpine.
# The image adds fontconfig (Skia looks fonts up through it) and free fonts with the same metrics as common office fonts:
#   Calibri -> Carlito, Cambria -> Caladea, Arial / Times New Roman / Courier New -> Liberation Sans / Serif / Mono;
#   DejaVu stands in for Verdana, Tahoma, Segoe UI and Consolas.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props README.md THIRD-PARTY-NOTICES.md ./
COPY src/ src/
RUN dotnet publish src/FastThumb.Cli/FastThumb.Cli.csproj -c Release -o /app -p:MinVerSkip=true

# the tool targets net8.0 and rolls forward to the runtime of this image
FROM mcr.microsoft.com/dotnet/runtime:10.0
ARG CJK_FONTS=false
RUN apt-get update \
 && apt-get install -y --no-install-recommends fontconfig fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea fonts-dejavu-core \
 && if [ "$CJK_FONTS" = "true" ]; then apt-get install -y --no-install-recommends fonts-noto-cjk; fi \
 && rm -rf /var/lib/apt/lists/*
# glibc gives every thread that allocates its own memory arena; with several sizes encoded at once that doubles peak
# memory. Two arenas keep about half of it for a few percent more time.
ENV MALLOC_ARENA_MAX=2
WORKDIR /app
COPY --from=build /app .
# only the natives of this platform are needed
RUN arch="$(dpkg --print-architecture)"; rid="linux-$( [ "$arch" = arm64 ] && echo arm64 || echo x64 )"; \
    find runtimes -mindepth 1 -maxdepth 1 ! -name "$rid" -exec rm -rf {} +
# The input is untrusted: no root inside the container (APP_UID is the non-root user of the .NET images). Contain it from
# outside too, so a file that exhausts memory ends this container, not the host:
#   docker run --rm --network none --read-only --tmpfs /tmp:size=512m --memory 2g --pids-limit 256 ...
USER $APP_UID
ENTRYPOINT ["dotnet", "FastThumb.Cli.dll"]
