#load "TypeShape.fs"  //  ignore-cat
#load "Printer.fs"    //  ignore-cat

open FsPad  // ignore-cat
open System
open System.IO
open System.Windows.Forms

type Results() =
    static let title = "FSI Results"
    static let localUrl () = Path.GetTempFileName () + ".fspad.html"
    static let getResultsWdw() =
            let localUrl = localUrl ()
            File.WriteAllText (localUrl, " use |> dump \"[title]\"")
            let brw = new WebBrowser (Dock = DockStyle.Fill,Url = Uri localUrl)
            let frm = new Form (Visible = true, Width = 256, Height = 768, Location = Drawing.Point (0, 0), Text = title)
            frm.Controls.Add brw
            brw
    static let mutable resultsWdw = getResultsWdw()
    static member Dump(objValue) = Results.Dump(objValue, 3)
    static member Dump(objValue, maxLevel : int) =
        let objName = "RESULTS !" 
        if resultsWdw.IsDisposed then resultsWdw <- getResultsWdw ()
        let localUrl = localUrl ()
        let html = Printer.Print(objValue, maxLevel)
        File.WriteAllText (localUrl, html)
        resultsWdw.FindForm().Text <- title + " - " + objName
        resultsWdw.Url <- Uri localUrl
        objValue
