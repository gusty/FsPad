#load "FsPad.fsx"

open FsPad
open System

///////////////////////////////////////
// Examples
///////////////////////////////////////

// Single value
Results.Dump 13

//  Text is Html Encoded
Results.Dump "<b>Hello World</b>"

// A list of values
Results.Dump [ 1 .. 30 ]

// A tuple
Results.Dump ( ("By Plane", 2, 250.99) )

// A list of tuples (notice how it changes the layout, to tabular)
Results.Dump [ ("By Plane", 2, 250.99); ("By Car", 10, 210.5);  ("By Train", 15, 483.53)  ]

// A record
type Person = { firstName : string   ; lastName : string; age : int; address : string } 
Results.Dump  { firstName = "Gustavo"; lastName = "Leon"; age = 43 ; address = "Dole" }

// A list of records (again changes to tabular)
Results.Dump
    [
        {firstName = "Gustavo"; lastName = "Leon"     ; age = 43 ; address = "Dole" }
        {firstName = "Eirik"  ; lastName = "Tsarpalis"; age =  5 ; address = "Dublin" }
    ]

// Nested stuff
type Dev = { firstName : string ; lastName : string ; age : int; address : string ; projects : string list } 
Results.Dump   {firstName = "Gustavo"; lastName = "Leon"; age = 43 ; address = "Dole" ; projects = ["F#+"; "ScrapeM" ]}

Results.Dump
    [
        {firstName = "Gustavo"; lastName = "Leon"     ; age = 43 ; address = "Dole"   ; projects = ["F#+"; "ScrapeM" ]}
        {firstName = "Eirik"  ; lastName = "Tsarpalis"; age =  5 ; address = "Dublin" ; projects = ["TypeShape"; "FsPickler" ]}
    ]

//  Recursively Defined Object
type Tree<'a>(value:'a, getEdges:'a -> seq<'a>) = 
    let list = lazy [ for e in getEdges(value) -> Tree(e, getEdges) ]
    member this.Value = value
    member this.Children = list.Value

let tree1 = Tree(1, fun x -> seq { for x in x..2 -> x })

Results.Dump(tree1, 9)