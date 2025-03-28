namespace FsAutoComplete

open System

module DotnetNewTemplate =
  type Template =
    { Name: string
      ShortName: string
      Language: TemplateLanguage list
      Tags: string list }

  and TemplateLanguage =
    | CSharp
    | FSharp
    | VB

  and DetailedTemplate =
    { TemplateName: string
      Author: string
      TemplateDescription: string
      Options: TemplateParameter list }

  and TemplateParameter =
    { ParameterName: string
      ShortName: string
      ParameterType: TemplateParameterType
      ParameterDescription: string
      DefaultValue: string }

  and TemplateParameterType =
    | Bool
    | String
    | Choice of string list

  let private getListCommand () =
    if System.Environment.Version.Major = 7 then
      "new list"
    else
      "new --list"

  let installedTemplates () : Template list =
    let readTemplates () =

      let si = System.Diagnostics.ProcessStartInfo()
      si.FileName <- "dotnet"

      si.Arguments <- $"{getListCommand ()} -lang F#"
      si.UseShellExecute <- false
      si.RedirectStandardOutput <- true
      si.WorkingDirectory <- Environment.CurrentDirectory
      si.EnvironmentVariables.["DOTNET_CLI_UI_LANGUAGE"] <- "en-us"
      let proc = System.Diagnostics.Process.Start(si)
      let mutable output = ""

      while not proc.StandardOutput.EndOfStream do
        let line = proc.StandardOutput.ReadLine()
        output <- output + Environment.NewLine + line

      output

    let parseTemplateOutput (x: string) =
      let xs =
        x.Split(Environment.NewLine)
        |> Array.skipWhile (fun n -> not (n.StartsWith("Template", StringComparison.Ordinal)))
        |> Array.filter (fun n -> not (n.StartsWith ' ' || String.IsNullOrWhiteSpace n))

      let header = xs.[0]
      let body = xs.[2..]
      let nameLength = header.IndexOf("Short", StringComparison.Ordinal)

      let body =
        body
        |> Array.map (fun (n: string) ->
          let name = n.[0 .. nameLength - 1].Trim()
          let shortName = n.[nameLength..].Split(' ').[0].Trim()
          name, shortName)

      body

    readTemplates ()
    |> parseTemplateOutput
    |> Array.map (fun (name, shortName) ->
      { Name = name
        ShortName = shortName
        Language = []
        Tags = [] })
    |> Array.toList

  let templateDetails () : DetailedTemplate list =
    [ { TemplateName = "Console Application"
        Author = "Microsoft"
        TemplateDescription =
          "A project for creating a command-line application that can run on .NET Core on Windows, Linux and macOS"
        Options =
          [ { ParameterName = "--no-restore"
              ShortName = ""
              ParameterType = TemplateParameterType.Bool
              ParameterDescription = "If specified, skips the automatic restore of the project on create."
              DefaultValue = "false / (*) true" } ] }

      { TemplateName = "Class library"
        Author = "Microsoft"
        TemplateDescription = "A project for creating a class library that targets .NET Standard or .NET Core"
        Options =
          [ { ParameterName = "--framework"
              ShortName = "-f"
              ParameterType =
                TemplateParameterType.Choice
                  [ "net8.0     - Target .net 8"; "netstandard2.0    - Target netstandard2.0" ]
              ParameterDescription = "The target framework for the project."
              DefaultValue = "netstandard2.0" }

            { ParameterName = "--no-restore"
              ShortName = ""
              ParameterType = TemplateParameterType.Bool
              ParameterDescription = "If specified, skips the automatic restore of the project on create."
              DefaultValue = "false / (*) true" }

            ] } ]

  let isMatch (filterstr: string) (x: string) = x.ToLower().Contains(filterstr.ToLower())

  let nameMatch (filterstr: string) (x: string) = x.ToLower() = filterstr.ToLower()

  let extractString (t: Template) = [ t.Name; t.ShortName ]

  let extractDetailedString (t: DetailedTemplate) = [ t.TemplateName ]

  let dotnetnewgetDetails (userInput: string) =
    let templates =
      templateDetails ()
      |> List.map (fun t -> t, extractDetailedString t)
      |> List.choose (fun (t, strings) ->
        if strings |> List.exists (nameMatch userInput) then
          Some t
        else
          None)

    match templates with
    | [] -> failwithf "No template exists with name : %s" userInput
    | [ x ] -> x
    | _ -> failwithf "Multiple templates found : \n%A" templates
