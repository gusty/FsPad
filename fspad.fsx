#r @"C:\Program Files (x86)\LINQPad5\LINQPad.exe"

open System
open System.IO
open System.Windows.Forms

let dumpClosure =
    let title = "FSI Results"
    let localUrl () = Path.GetTempFileName () + ".fspad.html"
    let getResultsWdw () =
        let localUrl = localUrl ()
        File.WriteAllText (localUrl, " use |> dump \"[title]\"")
        let brw = new WebBrowser (Dock = DockStyle.Fill,Url = Uri localUrl)
        let frm = new Form (Visible = true, Width = 256, Height = 768, Location = Drawing.Point (1024, 0), Text = title)
        frm.Controls.Add brw
        brw
    let mutable resultsWdw = getResultsWdw ()
    fun objName (objValue:obj) ->
        if resultsWdw.IsDisposed then resultsWdw <- getResultsWdw ()
        let localUrl = localUrl ()
        use writer = LINQPad.Util.CreateXhtmlWriter true
        writer.Write objValue
        File.WriteAllText (localUrl, string writer)
        resultsWdw.FindForm().Text <- title + " - " + objName
        resultsWdw.Url <- Uri localUrl
        objValue

let dump n v = 
    dumpClosure n (v :> obj) |> ignore
    v

// auto-printers
// fsi.AddPrinter (fun (o:System.Collections.IEnumerable) -> (o |> dump "seqs" |> string))
// fsi.AddPrinter (fun (o:obj) -> (o |> dump "all" |> string))