module FsHttp.Fsi

open System
open System.Collections.Generic
open System.Net.Http
open System.Text

open FsHttp.Domain


let noCustomPrinting (printHint: PrintHint) = 
    { printHint with isEnabled = false }

let noRequestHeader (printHint: PrintHint) = 
    { printHint with
        requestPrintHint =
            { printHint.requestPrintHint with printHeader = false } }

let noRequestBody (printHint: PrintHint) = 
    { printHint with
        requestPrintHint =
            { printHint.requestPrintHint with printBody = false } }

let noResponseHeader (printHint: PrintHint) = 
    { printHint with
        responsePrintHint =
            { printHint.responsePrintHint with printHeader = false } }

let withResponseContent (printHint: PrintHint) = 
    { printHint with
        responsePrintHint =
            { printHint.responsePrintHint with
                printContent =
                    { printHint.responsePrintHint.printContent with isEnabled = true } } }

let noResponseContentPrinting (printHint: PrintHint) = 
    { printHint with
        responsePrintHint =
            { printHint.responsePrintHint with
                printContent =
                    { printHint.responsePrintHint.printContent with isEnabled = false } } }

let noResponseContentFormatting (printHint: PrintHint) = 
    { printHint with
        responsePrintHint =
            { printHint.responsePrintHint with
                printContent =
                    { printHint.responsePrintHint.printContent with format = false } } }

let withResponseContentMaxLength maxLength (printHint: PrintHint) =
    { printHint with
        responsePrintHint = { printHint.responsePrintHint with
                                printContent = { printHint.responsePrintHint.printContent with 
                                                   maxLength = maxLength } } } 
    |> withResponseContent

// Printing (Response -> Response)
let modifyPrinter f (r: Response) =
    { r with
        request = { r.request with 
                      config = { r.request.config with 
                                   printHint = f r.request.config.printHint
                                   printptDebugMessages = true } } }

let rawPrinterTransformer = noCustomPrinting
let headerOnlyPrinterTransformer = noResponseContentPrinting
let showPrinterTransformer maxLength = (withResponseContentMaxLength maxLength >> withResponseContent)
let previewPrinterTransformer = withResponseContent
let expandPrinterTransformer = (withResponseContentMaxLength Int32.MaxValue >> withResponseContent)

// TODO: Printer for Request

type StringBuilder with
    member sb.append (s:string) = sb.Append s |> ignore
    member sb.appendLine s = sb.AppendLine s |> ignore
    member sb.newLine() = sb.appendLine ""
    member sb.appendSection s =
        sb.appendLine s
        String([0..s.Length] |> List.map (fun _ -> '-') |> List.toArray) |> sb.appendLine

let print (r: Response) =
    let sb = StringBuilder()
    
    let contentIndicator = "===content==="

    let printHeaderCollection (headers: KeyValuePair<string, string seq> seq) =
        let maxHeaderKeyLength =
            let lengths = headers |> Seq.map (fun h -> h.Key.Length) |> Seq.toList
            match lengths with
            | [] -> 0
            | list -> list |> Seq.max

        for h in headers do
            let values = String.Join(", ", h.Value)
            sb.appendLine (sprintf "%-*s: %s" (maxHeaderKeyLength + 3) h.Key values)

    let printRequest() =
        let requestPrintHint = r.request.config.printHint.requestPrintHint
        
        sb.appendSection "REQUEST"
        
        sprintf "%s %s HTTP/%s" (r.request.header.method.ToString()) (FsHttpUrl.toUriString r.request.header.url) (r.version.ToString())
        |> sb.appendLine

        if requestPrintHint.printHeader then
            let contentHeaders,multipartHeaders =
                if not (isNull r.requestMessage.Content) then
                    let a = r.requestMessage.Content.Headers |> Seq.toList
                    let b =
                        match r.requestMessage.Content with
                        | :? MultipartFormDataContent as m ->

                            // TODO: After having the request invoked, the dotnet multiparts
                            // have no headers anymore...

                            m
                            |> Seq.collect (fun part -> part.Headers)
                            |> Seq.toList
                        | _ -> []
                    a,b
                else
                    [],[]

            printHeaderCollection (
                (r.requestMessage.Headers |> Seq.toList)
                @ contentHeaders
                @ multipartHeaders)

        if requestPrintHint.printBody then
            let formatContentData contentData =
                match contentData with
                | StringContent s -> s
                | ByteArrayContent bytes ->
                    sprintf "::ByteArray (length = %d)" bytes.Length
                | StreamContent stream ->
                    sprintf "::Stream (length = %s)" (if stream.CanSeek then stream.Length.ToString() else "?")
                | FormUrlEncodedContent formDataList ->
                    [
                        yield "::FormUrlEncoded"
                        for kvp in formDataList do
                            yield sprintf "    %s = %s" kvp.Key kvp.Value
                    ]
                    |> String.concat "\n"
                | FileContent fileName ->
                    sprintf "::File (name = %s)" fileName

            sb.appendLine contentIndicator
            sb.appendLine <|
                match r.request.content with
                | Empty -> ""
                | Single bodyContent -> formatContentData bodyContent.contentData
                | Multi multipartContent ->
                    [
                        yield "::Multipart"
                        for contentData in multipartContent.contentData do
                            yield (sprintf "-------- %s" contentData.name)
                            yield sprintf "Part content type: %s" (match contentData.contentType with | Some v -> v | _ -> "")
                            yield formatContentData contentData.content
                    ]
                    |> String.concat "\n"
        
        sb.newLine()

    let printResponse() =
        sb.appendSection "RESPONSE"
        sb.appendLine (sprintf "HTTP/%s %d %s" (r.version.ToString()) (int r.statusCode) (string r.statusCode))

        if r.request.config.printHint.responsePrintHint.printHeader then
            printHeaderCollection ((r.headers |> Seq.toList) @ (r.content.Headers |> Seq.toList))

        if r.request.config.printHint.responsePrintHint.printContent.isEnabled then
            let trimmedContentText =
                try
                    let contentText =
                        if r.request.config.printHint.responsePrintHint.printContent.format then
                            Response.toFormattedText r
                        else
                            Response.toText r
                    let maxLength = r.request.config.printHint.responsePrintHint.printContent.maxLength
                    if contentText.Length > maxLength then
                        (contentText.Substring (0,maxLength)) + "\n..."
                    else
                        contentText
                with ex -> sprintf "ERROR reading response content: %s" (ex.ToString())
            sb.appendLine contentIndicator
            sb.append trimmedContentText
            
            sb.newLine()
    
    (sb.newLine >> printRequest >> printResponse)()
    sb.ToString()


module Init =

    type PrintableResponse = | PrintableResponse of Response

    let mutable private isInitialized = false
    
    // This seems like a HACK, but there shouldn't be the requirement of referencing FCS in FSI.
    let init() =
        if isInitialized then () else

        let fsiAssemblyName = "FSI-ASSEMBLY"

        let isInteractive =
            AppDomain.CurrentDomain.GetAssemblies()
            |> Array.exists (fun asm -> asm.IsDynamic && asm.GetName().Name.StartsWith(fsiAssemblyName))

        if isInteractive then
            let printTransformer (r:Response) =
                match r.request.config.printHint.isEnabled with
                | true -> (PrintableResponse r) :> obj
                | false -> null

            let printer (r:PrintableResponse) =
                let (PrintableResponse inner) = r
                try print inner
                with ex -> ex.ToString()

            AppDomain.CurrentDomain.GetAssemblies()
            |> Array.tryFind (fun x -> x.GetName().Name = "FSharp.Compiler.Interactive.Settings")
            |> Option.map (fun asm ->
                asm.ExportedTypes
                |> Seq.tryFind (fun t -> t.FullName = "FSharp.Compiler.Interactive.Settings")
                |> Option.map (fun settings ->
                    settings.GetProperty("fsi")
                    |> Option.ofObj
                    |> Option.map (fun x -> x.GetValue(null)))
            )
            |> Option.flatten
            |> Option.flatten
            |> function
                | None ->
                    printfn "--- FsHttp: FSI object not found (this is expected when running in a notebook)."
                | Some fsiInstance ->
                    let t = fsiInstance.GetType()

                    let addPrintTransformer = t.GetMethod("AddPrintTransformer").MakeGenericMethod([| typeof<Response> |])
                    let addPrinter = t.GetMethod("AddPrinter").MakeGenericMethod([| typeof<PrintableResponse> |])
        
                    addPrinter.Invoke(fsiInstance, [| printer |]) |> ignore
                    addPrintTransformer.Invoke(fsiInstance, [| printTransformer |]) |> ignore
                    
                    printfn "--- FsHttp: Printer successfully registered."
            
        isInitialized <- true
