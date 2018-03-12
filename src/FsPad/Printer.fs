namespace FsPad

open System
open TypeShape

module Printer = 
    open Representation
    open Reflection

    //type Schema = string
    type TypedNode = LabelledNode<Schema>
    type PrettyPrinter<'T> = int -> 'T -> TypedNode

// Generic value to PrettyPrint

    let rec mkPrinter<'T> () : PrettyPrinter<'T> =
        let ctx = new RecTypeManager()
        mkPrinterCached<'T> ctx

    and private mkPrinterCached<'T> (ctx : RecTypeManager) : PrettyPrinter<'T> =
        match ctx.TryFind<PrettyPrinter<'T>> () with
        | Some p -> p
        | None ->
            let _ = ctx.CreateUninitialized<PrettyPrinter<'T>>(fun c t -> c.Value t)
            let p = mkPrinterAux<'T> ctx
            ctx.Complete p

    and private mkPrinterAux<'T> (ctx : RecTypeManager) : PrettyPrinter<'T> =
        let wrap(p : PrettyPrinter<'a>) = unbox<PrettyPrinter<'T>> p
        let wrapNested(p : PrettyPrinter<'a>) = unbox<PrettyPrinter<'T>>(fun level x -> if level <= 0 then Chunk (generateSchema typeof<'T>, Guid()) else p level x)

        let mkFieldPrinter (field : IShapeMember<'DeclaringType>) =
            field.Accept {
                new IMemberVisitor<'DeclaringType, string * (PrettyPrinter<'DeclaringType>)> with
                    member __.Visit(field : ShapeMember<'DeclaringType, 'Field>) =
                        let fp = mkPrinterCached<'Field> ctx
                        field.Label, (fun l x -> x |> field.Project |> fp l)
            }

        match shapeof<'T> with
        | Shape.Unit     -> wrap (fun _ _        -> Scalar (generateSchema typeof<unit>    , "()"))
        | Shape.Bool     -> wrap (fun _ v        -> Scalar (generateSchema typeof<Boolean> , sprintf "%b" v))
        | Shape.Byte     -> wrap (fun _ (v:byte) -> Scalar (generateSchema typeof<Byte> , sprintf "%duy" v))
        | Shape.Char     -> wrap (fun _ v        -> Scalar (generateSchema typeof<char> , string<char> v))
        | Shape.Int16    -> wrap (fun _ v        -> Scalar (generateSchema typeof<int16>, string<int16> v))
        | Shape.Int32    -> wrap (fun _ v        -> Scalar (generateSchema typeof<int>  , string<int> v))
        | Shape.Int64    -> wrap (fun _ v        -> Scalar (generateSchema typeof<int64>, string<int64> v))
        | Shape.Double   -> wrap (fun _ v        -> Scalar (generateSchema typeof<float>, string<float> v))
        | Shape.String   -> wrap (fun _ (v:string)   -> Scalar (generateSchema typeof<String>, v))
        | Shape.DateTime -> wrap (fun _ (b:DateTime) -> Scalar (generateSchema typeof<DateTime>, sprintf "(%i, %i, %i, %i, %i, %i, %i)" b.Year b.Month b.Day b.Hour b.Minute b.Second b.Millisecond))

        | Shape.FSharpList s ->
            s.Accept { new IFSharpListVisitor<PrettyPrinter<'T>> with
                        member __.Visit<'a> () = 
                            let tp = mkPrinterCached<'a> ctx 
                            wrapNested (fun level x -> Sequence (generateSchema typeof<'T>, x |> List.map (tp (level - 1)))) }

        | Shape.Array s when s.Rank = 1 ->
            s.Accept { new IArrayVisitor<PrettyPrinter<'T>> with 
                        member __.Visit<'a> _  = 
                            let tp = mkPrinterCached<'a> ctx 
                            wrapNested (fun level x -> Sequence (generateSchema typeof<'T>, x |> Array.map (tp (level - 1)) |> Array.toList)) }

        | Shape.FSharpSet s ->
            s.Accept { new IFSharpSetVisitor<PrettyPrinter<'T>> with 
                        member __.Visit<'a when 'a : comparison> () = 
                            let tp = mkPrinterCached<'a> ctx 
                            wrapNested (fun level (s : Set<'a>) -> Sequence (generateSchema typeof<'T>, s |> Seq.map (tp (level - 1)) |> Seq.toList)) }

        | Shape.Tuple (:? ShapeTuple<'T> as shape) ->
            let elemPrinters = shape.Elements |> Array.map mkFieldPrinter
            wrapNested(fun level (t:'T) -> 
                Mapping (generateSchema typeof<'T>, (elemPrinters |> Seq.map (fun (n, ep) -> {name = n.Replace("Item", "#"); value = ep (level - 1) t}) |> Seq.toList)))

        | Shape.FSharpRecord (:? ShapeFSharpRecord<'T> as shape) ->
            let fieldPrinters = shape.Fields |> Array.map mkFieldPrinter
            wrapNested(fun level (r:'T) -> 
                Mapping (generateSchema typeof<'T>, (fieldPrinters |> Seq.map (fun (name, ep) -> {name = name; value = ep (level - 1) r} ) |> Seq.toList)))


        | Shape.FSharpUnion (:? ShapeFSharpUnion<'T> as shape) ->
            let mkUnionCasePrinter (s : ShapeFSharpUnionCase<'T>) =
                let fieldPrinters = s.Fields |> Array.map mkFieldPrinter
                fun level (u:'T) -> 
                    match fieldPrinters with
                    | [|_,fp|] -> Mapping (generateSchema typeof<'T>, [ {name = "Case"; value = Scalar (generateSchema typeof<string>, s.CaseInfo.Name)}; {name = "Args"; value = Sequence (generateSchema typeof<'T>, [fp (level - 1) u])}])
                    | fps      -> Mapping (generateSchema typeof<'T>, [ {name = "Case"; value = Scalar (generateSchema typeof<string>, s.CaseInfo.Name)}; {name = "Args"; value = Sequence (generateSchema typeof<'T>, (fps |> Seq.map (fun (name, fp) -> fp (level - 1) u ) |> Seq.toList))}])

            let casePrinters = shape.UnionCases |> Array.map mkUnionCasePrinter
            fun level (u:'T) -> 
                let printer = casePrinters.[shape.GetTag u]
                printer (level - 1) u

        | Shape.Poco (:? ShapePoco<'T> as shape) ->
            let propPrinters = shape.Properties |> Array.map mkFieldPrinter
            wrapNested(
                fun level (r:'T) ->
                    Mapping (generateSchema typeof<'T>, propPrinters
                    |> Seq.map (fun (name, ep) ->
                        let value = ep (level - 1) r
                        {name = name; value = value}  ) |> Seq.toList)
            )

        | _ -> failwithf "unsupported type '%O'" typeof<'T>

    //---------------------------------------

    let pprint (level) (x:'t) = let p = mkPrinter<'t>() in p level x

    //---------------------------------------
