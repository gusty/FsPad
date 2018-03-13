#load "src\FsPad\FsPad.fsx"

open FsPad
open System

///////////////////////////////////////
// Examples
///////////////////////////////////////

// Single value
dump 13

//  Text is Html Encoded
dump "<b>Hello World</b>"

// A list of values
dump [ 1 .. 30 ]

// A tuple
dump ( ("By Plane", 2, 250.99) )

// A list of tuples (notice how it changes the layout, to tabular)
dump [ ("By Plane", 2, 250.99); ("By Car", 10, 210.5);  ("By Train", 15, 483.53)  ]

// A record
type Person = { firstName : string   ; lastName : string; age : int; address : string }
dump { firstName = "Gustavo"; lastName = "Leon"; age = 43 ; address = "Dole" }

// A list of records (again changes to tabular)
dump
    [
        {firstName = "Gustavo"; lastName = "Leon"  ; age = 43 ; address = "Dole" }
        {firstName = "Steve"  ; lastName = "Goguen"; age = 20 ; address = "?" }
    ]

// Nested stuff
type Dev = { firstName : string ; lastName : string ; age : int; address : string ; projects : string list }
dump {firstName = "Gustavo"; lastName = "Leon"; age = 43 ; address = "Dole" ; projects = ["F#+"; "ScrapeM" ]}

dump
    [
        {firstName = "Gustavo"; lastName = "Leon"  ; age = 43 ; address = "Dole"   ; projects = ["F#+"; "ScrapeM" ; "FsPad" ]}
        {firstName = "Steve"  ; lastName = "Goguen"; age = 20 ; address = "?" ; projects = ["Steego.NET"; "FsPad" ]}
    ]

////  Recursively Defined Object
//type Tree<'a>(value:'a, getEdges:'a -> seq<'a>) =
//    let list = lazy [ for e in getEdges(value) -> Tree(e, getEdges) ]
//    member this.Value = value
//    member this.Children = list.Value

//let tree1 = Tree(Some 1, fun (Some x) -> seq { for x in x..2 -> Some x })

//dump (tree1, 9)

// List of option
[Some 1; None; Some 4] |> dump

// List of tuples, some of them are options
[1..30] |> List.map (fun x -> ( (if x % 2 = 0 then Some x else None), x, if x % 3 = 0 then Some x else None) ) |> dump

// List of record, some fields are options
type Tab<'a,'b,'c> = { a : 'a ; b : 'b ; c : 'c }
[1..30] |> List.map (fun x -> {a = (if x % 2 = 0 then Some x else None); b = x; c = if x % 3 = 0 then Some x else None} ) |> dump