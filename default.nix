{
  stdenv,
  dotnet-sdk_8,
  buildFHSUserEnv,
  writers,
}: let
  dotnet = dotnet-sdk_8;
in
  buildFHSUserEnv {
    name = "ozma-report-generator";
    targetPkgs = pkgs:
      with pkgs; [
        alejandra
        dotnet
        zlib
        mono
        lttng-ust
        libunwind
        krb5
        lldb
        openssl
        unoconv
        # Required for vsdbg
        icu
      ];
    extraOutputsToInstall = ["dev"];
    profile = ''
      export DOTNET_ROOT=${dotnet}
    '';
    runScript = writers.writeBash "run-script" ''
      if [ "$#" = 0 ]; then
        exec "''${SHELL:-bash}"
      else
        exec "$@"
      fi
    '';
  }
