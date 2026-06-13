namespace Fsy.Cli

open Argu

type ScriptArgs =
  | [<AltCommandLine("-c"); Inherit>] Cache_Dir of string
  | [<Inherit>] Shadow_Dir of string
  | [<Inherit>] No_Cache
  | [<AltCommandLine("-s"); Inherit>] Symbol of string
  | [<Last; CliPrefix(CliPrefix.None); MainCommand>] Script of ``script.fsx``: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Script _ -> "Path of the F# script to run/compile"
      | Cache_Dir _ -> "Sets the cache directory. Default: ./.fsy"
      | No_Cache -> "Clears the cache and forces re-compilation"
      | Symbol _ ->
        "Allows defining symbols that can be used e.g. in #if directives. Use multiple times to define many symbols"
      | Shadow_Dir _ -> "Script shadow root dir. Default: cwd"


type InstallFsxExtensionsArgs =
  | [<AltCommandLine("-t"); Inherit>] Target_Dir of string
  | [<AltCommandLine("-f"); Inherit>] Framework_Version of FrameworkVersion

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Target_Dir _ -> "Installation path. Default: ~/.fsharp/fsx-extensions/.fsch"
      | Framework_Version _ -> "Framework version. Default: net10"

and FrameworkVersion =
  | Net10
  | Net9

type Args =
  | [<AltCommandLine("-v"); Inherit>] Verbose
  | [<CliPrefix(CliPrefix.None); AltCommandLine("ifsx")>] Install_Fsx_Extensions of
    ParseResults<InstallFsxExtensionsArgs>
  | [<CliPrefix(CliPrefix.None)>] Run of ParseResults<ScriptArgs>
  | [<CliPrefix(CliPrefix.None); SubCommand>] Version

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Run _ -> "Runs the script in-process"
      | Version -> "Displays version"
      | Install_Fsx_Extensions _ ->
        "Copies the dlls required for editor support to a stable location: ~/.fsharp/fsx-extensions/.fsch"
      | Verbose -> "Shows some log messages"
