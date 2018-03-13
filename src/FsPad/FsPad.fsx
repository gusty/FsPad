#load "FsHtml.fs"
      "Representation.fs"
      "TypeShape.fs"
      "Printer.fs"
      "StaticHtml.fs"

open FsPad

open System
open System.IO
open System.Windows.Forms

open Representation

type Results() =
    static let title = "FsPad"
    static let localUrl () = Path.GetTempFileName () + ".fspad.html"
    static let getResultsWdw() =
            let localUrl = localUrl ()
            File.WriteAllText (localUrl, " use |> dump")
            let brw = new WebBrowser (Dock = DockStyle.Fill,Url = Uri localUrl)
            let frm = new Form (Visible = true, Width = 256, Height = 768, Location = Drawing.Point (0, 0), Text = title)
            frm.Controls.Add brw
            brw
    static let mutable resultsWdw = getResultsWdw()
    static member ShowHtml(html: string) =
        if resultsWdw.IsDisposed then resultsWdw <- getResultsWdw ()
        let localUrl = localUrl ()
        File.WriteAllText (localUrl, html)
        resultsWdw.FindForm().Text <- title
        resultsWdw.Url <- Uri localUrl
        ()

let show x = Results.ShowHtml(x)

let render node =
    StaticHtml.renderWithStaticHeader node
    |> string

let print level value = Printer.pprint level value

type Results with
    static member Print<'a> (level: int) (value: 'a) =
        print level value
        |> render
        |> show

    static member PrintAll<'a> (value: 'a) =
        Results.Print 100 value

let dump (value: 'a) = Results.PrintAll<'a>(value)