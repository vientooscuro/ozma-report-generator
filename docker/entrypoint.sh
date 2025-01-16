#!/usr/bin/env bash
set -e

if ! [ -e /etc/ozma-report-generator/config.json ]; then
  if [ -z "$DB_HOST" ]; then
    echo "DB_HOST must be set" >&2
    exit 1
  fi
  if [ -z "$DB_PORT" ]; then
    DB_PORT=5432
  fi
  if [ -z "$DB_USER" ]; then
    echo "DB_USER must be set" >&2
    exit 1
  fi
  if [ -z "$DB_PASSWORD" ]; then
    echo "DB_PASSWORD must be set" >&2
    exit 1
  fi
  if [ -z "$DB_NAME" ]; then
    echo "DB_NAME must be set" >&2
    exit 1
  fi

  if [ -z "$EXTERNAL_ORIGIN" ]; then
    # Backward compatibility
    if [ -n "$ORIGIN" ]; then
      EXTERNAL_ORIGIN="$ORIGIN"
    elif [ -n "$EXTERNAL_HOSTPORT" ]; then
      EXTERNAL_ORIGIN="${EXTERNAL_PROTOCOL:-http}://${EXTERNAL_HOSTPORT}"
    else
      echo "EXTERNAL_ORIGIN must be set" >&2
      exit 1
    fi
  fi

  if [ -z "$AUTH_AUTHORITY" ]; then
    if [ -n "$EXTERNAL_ORIGIN" ]; then
      AUTH_AUTHORITY="${EXTERNAL_ORIGIN}/auth/realms/ozma"
    else
      echo "AUTH_AUTHORITY must be set" >&2
      exit 1
    fi
  fi

  if [ -z "$AUTH_CLIENT_ID" ]; then
    echo "AUTH_CLIENT_ID must be set" >&2
    exit 1
  fi

  if [ -z "$OZMA_DB_URL" ]; then
    if [ -n "$OZMA_DB_HOSTPORT" ]; then
      OZMA_DB_URL="${OZMA_DB_PROTOCOL:-http}://${OZMA_DB_HOSTPORT}"
    else
      echo "OZMA_DB_URL must be set" >&2
      exit 1
    fi
  fi

  mkdir -p /etc/ozma-report-generator
  jq -n \
    --arg dbHost "$DB_HOST" \
    --argjson dbPort "$DB_PORT" \
    --arg dbUser "$DB_USER" \
    --arg dbPassword "$DB_PASSWORD" \
    --arg dbName "$DB_NAME" \
    --arg authAuthority "$AUTH_AUTHORITY" \
    --arg authMetadataAddress "$AUTH_METADATA_ADDRESS" \
    --arg authClientId "$AUTH_CLIENT_ID" \
    --arg authClientSecret "$AUTH_CLIENT_SECRET" \
    --argjson authRequireHttpsMetadata "${AUTH_REQUIRE_HTTPS_METADATA:-true}" \
    --arg pathBase "$PATH_BASE" \
    --arg origin "$EXTERNAL_ORIGIN" \
    --arg ozmadbUrl "$OZMA_DB_URL" \
    --arg ozmadbForceInstance "$OZMA_DB_FORCE_INSTANCE" \
    '{
      "kestrel": {
        "endPoints": {
          "http": {
            "url": "http://0.0.0.0:5000"
          }
        }
      },
      "connectionStrings": {
        "postgreSql": "host=\($dbHost); port=\($dbPort); Username=\($dbUser); Password=\($dbPassword); Database=\($dbName)"
      },
      "authSettings": ({
        "authority": $authAuthority,
        "clientId": $authClientId,
        "requireHttpsMetadata": $authRequireHttpsMetadata,
      } + (if $authMetadataAddress == "" then {} else { "metadataAddress": $authMetadataAddress } end)
        + (if $authClientSecret == "" then {} else { "clientSecret": $authClientSecret } end)),
      "hostSettings": ({
        "allowedOrigins": [$origin]
      } + (if $pathBase == "" then {} else { "pathBase": $pathBase } end)),
      "ozmaDBSettings": ({
        "databaseServerUrl": $ozmadbUrl
      } + (if $ozmadbForceInstance == "" then {} else { "forceInstance": $ozmadbForceInstance } end)),
    }' > /etc/ozma-report-generator/config.json
fi

export ASPNETCORE_CONTENTROOT=/opt/ozma-report-generator
exec /opt/ozma-report-generator/OzmaReportGenerator /etc/ozma-report-generator/config.json
