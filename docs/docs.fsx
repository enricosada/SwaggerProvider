﻿// --------------------------------------------------------------------------------------
// Fable documentation build script
// --------------------------------------------------------------------------------------

#load "../packages/build/FSharp.Formatting/FSharp.Formatting.fsx"
#I "../packages/build/FAKE/tools/"
#I "../packages/build/Suave/lib/net40"
#I "../packages/build/DotLiquid/lib/NET451"
#r "FakeLib.dll"
#r "Suave.dll"
#r "DotLiquid.dll"
#load "liquid.fs"
#load "helpers.fs"
open Fake
open System
open System.IO
open System.Text.RegularExpressions
open FSharp.Literate
open FSharp.Markdown
open Suave
open Suave.Web
open Suave.Http
open System.IO
open Helpers
open Fake.Git

// --------------------------------------------------------------------------------------
// Global definitions and folders
// --------------------------------------------------------------------------------------

// Where to push generated documentation
let publishSite = "http://fsprojects.github.io/SwaggerProvider/"
let githubLink = "https://github.com/fsprojects/SwaggerProvider"
let publishBranch = "gh-pages"

// Paths with template/source/output locations
let source      = __SOURCE_DIRECTORY__ </> "source"
let templates   = __SOURCE_DIRECTORY__ </> "templates"
let output      = __SOURCE_DIRECTORY__ </> "output"
let contentPage = "content.html"
let root   = __SOURCE_DIRECTORY__ </> ".." |> Path.GetFullPath
let temp        = root </> "temp"

// Set templates directory for DotLiquid
DotLiquid.setTemplatesDirs [templates]
DotLiquid.Template.NamingConvention <- DotLiquid.NamingConventions.CSharpNamingConvention()

// --------------------------------------------------------------------------------------
// Markdown pre-processing
// --------------------------------------------------------------------------------------

/// Extract heading from the document
let extractHeading name paragraphs =
    let heading, other =
      paragraphs
      |> List.map (function
        | Heading(1, [Literal text]) -> Choice1Of2 text
        | p -> Choice2Of2 p)
      |> List.partition (function Choice1Of2 _ -> true | _ -> false)
    match heading, other with
    | [Choice1Of2 text], pars -> text, List.map (function Choice2Of2 p -> p | _ -> failwith "unexpected") pars
    | _ -> failwithf "Document '%s' does not contain parseable top-level heading." name

/// Extracts list with metadata from a document
let extractAttributes name (doc:MarkdownDocument) paragraphs =
    match paragraphs with
    | ListBlock(_, lis)::pars ->
        [ for li in lis do
            match li with
            // The <li> is either Span (for short items) or Paragraph (if there
            // are any long items). We extract key from "key: value" in the first
            // paragraph. Then we format short things as Span and long as Paragraph
            // (black magic to make sure we emit nice HTML....)
            | Paragraph (Literal s::spans)::pars
            | Span (Literal s::spans)::pars when s.Contains(":") ->
                let col = s.IndexOf(":")
                let wrap = match li with (Span _)::_ | [Paragraph _] -> Span | _ -> Paragraph
                let rest = wrap(Literal (s.Substring(col+1))::spans)::pars
                yield s.Substring(0, col).Trim(), Markdown.WriteHtml(MarkdownDocument(rest, doc.DefinedLinks)).Trim()
            | _ -> failwithf "Document '%s' has unsupported formatting in header block: %A" name li ],
        pars
    | _ -> failwithf "Document '%s' is missing header block." name


// --------------------------------------------------------------------------------------
// Generating static parts of the web site
// --------------------------------------------------------------------------------------

/// Simple model for processed Markdown documents
type Page =
  { Root : string
    Active : string
    Heading : string
    Tagline : string
    Content : string }

// Copy static files from the 'source' folder to 'output' folder (add more extensions!)
let copyFiles force =
    Helpers.processDirectory force source output [".css"; ".js"; ".png"; ".gif"; ".jpg"; ""]
      (fun source outdir ->
          let name = Path.GetFileName(source)
          File.Copy(source, outdir </> name, true) )

// Build documentation from Markdown files in `content`
let processMarkdown siteRoot force =
    Helpers.processDirectory force source output [".md"]
      (fun source outdir ->
          let name = Path.GetFileNameWithoutExtension(source)
          printfn "Processing markdown file: %s.md" name
          use tmp = TempFile.New()
          let attrs = System.Collections.Generic.Dictionary<_, _>()
          let heading = ref ""
          Literate.ProcessMarkdown(source, output=tmp.Name, generateAnchors=true, customizeDocument = fun _ doc ->
              let kvps, pars = extractAttributes name doc.MarkdownDocument doc.Paragraphs
              for k, v in kvps do attrs.Add(k, v)
              let htext, pars = extractHeading name pars
              heading.Value <- htext
              doc.With(pars) )
          let html =
              { Root = siteRoot
                Active = if attrs.ContainsKey("active") then attrs.["active"] else name
                Tagline = attrs.["tagline"]
                Heading = heading.Value
                Content = File.ReadAllText(tmp.Name) }
              |> DotLiquid.page contentPage
          File.WriteAllText(outdir </> name + ".html", html))

// Build documentation from HTML files in `content` (just apply templating)
let processHtml siteRoot force =
    Helpers.processDirectory force source output [".html"]
      (fun source outdir ->
          let name = Path.GetFileNameWithoutExtension(source)
          printfn "Processing html file: %s.html" name
          let html =
            { Active = name; Content = ""; Tagline = ""; Heading = ""; Root = siteRoot  }
            |> DotLiquid.page source
          File.WriteAllText(outdir </> name + ".html", html))

// Generate all static parts of the web site (this is pretty fast)
let generateStaticPages siteRoot force () =
    traceImportant "Updating static pages"
    copyFiles force
    processMarkdown siteRoot force
    processHtml siteRoot force
    traceImportant "Updating static pages completed"


// --------------------------------------------------------------------------------------
// Local Suave server for hosting page during development (with WebSockets refresh!)
// --------------------------------------------------------------------------------------

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Suave.Operators

let refreshEvent = new Event<unit>()

let socketHandler (webSocket : WebSocket) cx = socket {
    while true do
      let! refreshed =
        Control.Async.AwaitEvent(refreshEvent.Publish)
        |> Suave.Sockets.SocketOp.ofAsync
      do! webSocket.send Text (Suave.Utils.ASCII.bytes "refreshed") true }

let startWebServer () =
    let port = 8911
    let serverConfig =
        { defaultConfig with
           homeFolder = Some (FullName output)
           bindings = [ HttpBinding.mkSimple HTTP "127.0.0.1" port ] }
    let app =
      choose [
        Filters.path "/websocket" >=> handShake socketHandler
        Writers.setHeader "Cache-Control" "no-cache, no-store, must-revalidate"
        >=> Writers.setHeader "Pragma" "no-cache"
        >=> Writers.setHeader "Expires" "0"
        >=> choose [ Files.browseHome; Filters.path "/" >=> Files.browseFileHome "index.html" ] ]

    let addMime f = function
      | ".wav" -> Writers.mkMimeType "audio/wav" false
      | ".tsv" -> Writers.mkMimeType "text/tsv" false | ext -> f ext
    let app ctx = app { ctx with runtime = { ctx.runtime with mimeTypesMap = addMime ctx.runtime.mimeTypesMap } }

    startWebServerAsync serverConfig app |> snd |> Async.Start
    System.Diagnostics.Process.Start (sprintf "http://localhost:%d/index.html" port) |> ignore


// --------------------------------------------------------------------------------------
// FAKE targets for generating and releasing documentation
// --------------------------------------------------------------------------------------

Target "CleanDocs" (fun _ ->
    CleanDirs [output]
)

Target "GenerateDocs" (fun _ ->
    generateStaticPages publishSite true ()
)

Target "BrowseDocs" (fun _ ->
    // Update static pages & sample pages (but don't recompile JS)
    let root = "http://localhost:8911"
    generateStaticPages root true ()

    // Setup watchers to regenerate things as needed
    let watchAndRefresh f = WatchChanges (fun _ ->
      try f(); refreshEvent.Trigger() with e -> traceException e)
    use w1 = !! (source + "/**/*.*") |> watchAndRefresh (generateStaticPages root false)

    // Start local server
    startWebServer ()
    traceImportant "Waiting for page edits. Press ^C to kill the process."
    System.Threading.Thread.Sleep(-1)
)

let publishDocs() =
  CleanDir temp
  Repository.cloneSingleBranch "" (githubLink + ".git") publishBranch temp

  CopyRecursive output temp true |> tracefn "%A"
  StageAll temp
  Git.Commit.Commit temp (sprintf "Update site (%s)" (DateTime.Now.ToShortDateString()))
  Branches.push temp

Target "PublishDocs" (fun _ ->
    publishDocs()
)

Target "PublishStaticPages" (fun _ ->
    generateStaticPages publishSite true ()
    publishDocs()
)

// --------------------------------------------------------------------------------------
// Regenerate all docs when publishing, by default just generate & browse
// --------------------------------------------------------------------------------------

"CleanDocs"
  ==> "GenerateDocs"
  ==> "PublishDocs"

RunTargetOrDefault "BrowseDocs"
