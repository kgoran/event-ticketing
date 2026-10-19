#!/usr/bin/env bash
# Regenerates the fan site's PublicApi client from the committed OpenAPI document (ADR-0037).
# Generated code is committed and never edited by hand; product-ci runs this script and fails if
# the result differs from what is committed. Usage, from anywhere:
#   bash product/src/Frontends/FanSite/generate-client.sh
set -euo pipefail
cd "$(dirname "$0")/../../../.."   # the repository root: Kiota records paths relative to it

export KIOTA_OFFLINE_ENABLED=true KIOTA_TUTORIAL_ENABLED=false
dotnet tool restore > /dev/null

dotnet kiota generate \
  --language CSharp \
  --openapi product/src/Hosts/PublicApi/openapi/PublicApi.json \
  --class-name PublicApiClient \
  --namespace-name EventTicketing.FanSite.PublicApi \
  --output product/src/Frontends/FanSite/PublicApi \
  --exclude-backward-compatible \
  --clean-output \
  --log-level Warning
rm -f product/src/Frontends/FanSite/PublicApi/.kiota.log
