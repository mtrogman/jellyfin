ARG BASE_TAG=10.11.5

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .

RUN dotnet restore

RUN dotnet publish Jellyfin.Server \
  --configuration Release \
  --output /out \
  --self-contained false \
  -p:DebugSymbols=false \
  -p:DebugType=none \
  -p:TreatWarningsAsErrors=false \
  -p:RunAnalyzers=false \
  -p:NoWarn=CA1865

FROM jellyfin/jellyfin:${BASE_TAG}
COPY --from=build /out/ /jellyfin/
