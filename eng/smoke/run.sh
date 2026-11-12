#!/usr/bin/env sh
# Installs the freshly packed FastThumb package the way a user would and runs it: a new console app (net8.0 and net10.0)
# with a PackageReference to FastThumb makes thumbnails of a sheet, a PDF and, when tests/fixtures/video has one, a video.
#
#   sh eng/smoke/run.sh <package dir>
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PACKAGES="$(cd "${1:?usage: run.sh <package dir>}" && pwd)"
# dotnet on Windows needs C:/... rather than the /c/... of Git Bash
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
version="$(ls "$PACKAGES"/FastThumb.[0-9]*.nupkg | head -n 1 | sed -E 's/.*FastThumb\.(.*)\.nupkg/\1/')"
echo "FastThumb $version from $PACKAGES"

WORK="$(mktemp -d 2>/dev/null || mktemp -d -t fastthumb-smoke)"
trap 'rm -rf "$WORK"' EXIT
cp "$ROOT/eng/smoke/Program.cs" "$WORK/"
cat > "$WORK/Smoke.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FastThumb" Version="$version" />
  </ItemGroup>
</Project>
EOF
# isolate the app from the repository's build settings and package sources
echo '<Project />' > "$WORK/Directory.Build.props"
echo '<Project />' > "$WORK/Directory.Packages.props"
cat > "$WORK/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$(native "$PACKAGES")" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

video=""
for candidate in "$ROOT"/tests/fixtures/video/*.mp4; do [ -f "$candidate" ] && video="$candidate" && break; done
for tf in net8.0 net10.0; do
  echo "== app ($tf)"
  dotnet run --project "$WORK/Smoke.csproj" -c Release -f "$tf" -- "$video"
done

