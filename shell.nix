{pkgs ? import <nixpkgs> {}}: let
  dotnet = pkgs.dotnet-sdk_8;
  nodejs = pkgs.nodejs_22;
  python = pkgs.python3;

  env = pkgs.buildFHSUserEnv {
    name = "report-generator";
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
    runScript = pkgs.writeScript "env-shell" ''
      #!${pkgs.stdenv.shell}
      exec ${userShell}
    '';
  };

  userShell = builtins.getEnv "SHELL";
in
  pkgs.stdenv.mkDerivation {
    name = "report-generator-fhs-dev";

    shellHook = ''
      exec ${env}/bin/report-generator
    '';
    buildCommand = "exit 1";
  }
