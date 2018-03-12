namespace FsPad

open System

module Representation = 

    /// A primitive value that has a JavaScript representation.
    type Primitive = obj

    type ChunkId = System.Guid

    /// A named field with a value (i.e. a field or a column in the UI)
    type FieldValue<'label> = { name: string; value: LabelledNode<'label> }

    /// Tree representing the value with extra information.
    and LabelledNode<'label> = 
        /// Primitive value.
        | Scalar of 'label * Primitive
        /// A sequence of values
        | Sequence of 'label * LabelledNode<'label> list
        /// A complex value
        | Mapping of 'label * FieldValue<'label> list
        /// A value to be calculated lazily - we will maintain a collection
        /// of chunks keyed by ids to be evaluated on demand, though we'd need
        /// to have a server around for that.
        | Chunk of 'label * ChunkId
        member this.Label = 
            match this with
            | Scalar (lbl, _) 
            | Sequence (lbl, _)
            | Mapping (lbl, _)
            | Chunk (lbl, _) -> lbl

    /// Type of the named field. 
    type FieldType<'label> = { name: string; schema: 'label }

    /// Discriminates different variants of types in F# that we might want to
    /// distinguish in the UI.
    type Variant = 
        | Primitive
        | Poco
        | Record
        | Union
        | Collection
        | Tuple
        | Function

    /// This can be used to match a template for the display.
    /// It should be expressive enough for us to be able to have different templates for:
    /// - discriminated unions in general
    /// - options specifically
    /// - int options (if it somehow made sense to have something very specific for them)
    type TypePattern = 
        {
            typeName: string
            variant: Variant
            genericTypeArgs: TypePattern list
        }
        member pattern.DisplayName =
            match pattern.genericTypeArgs with 
            | [] -> sprintf "%s" pattern.typeName
            | other -> 
                if pattern.variant = Variant.Tuple then
                    String.Join(" * ", List.map (fun (x: TypePattern) -> x.DisplayName) other)
                else
                    let args = 
                        String.Join(",", List.map (fun (x: TypePattern) -> x.DisplayName) other)
                    sprintf "%s<%s>" pattern.typeName args

    /// Captures information about the type.
    type Schema =
        {
            /// .NET reflected type name (or an F# shorthand where we care)
            fullTypeName: string
            /// A structural representation of the type rich enough to generate table
            /// headers from.
            structuralType: FieldType<Schema> list
            /// Pattern to match a UI template on for display.
            typePattern: TypePattern
        }
        member this.DisplayName = this.typePattern.DisplayName

    type TypedNode = LabelledNode<Schema>

module Reflection = 
    
    open Representation
    open System.Reflection
    open FSharp.Reflection
    open System.Collections

    type Variant with
        static member FromType(typ: Type) = 
            let ienum = typeof<IEnumerable>
            match typ with
            | _ when typ = typeof<string>        -> Variant.Primitive
            | _ when ienum.IsAssignableFrom(typ) -> Variant.Collection
            | _ when FSharpType.IsFunction(typ)  -> Variant.Function
            | _ when FSharpType.IsRecord(typ)    -> Variant.Record
            | _ when FSharpType.IsTuple(typ)     -> Variant.Tuple
            | _ when FSharpType.IsUnion(typ)     -> Variant.Union
            | _ when typ.IsPrimitive             -> Variant.Primitive
            | _ -> Variant.Poco

    let rec generateTypePattern (typ: Type) : TypePattern =
        {
            typeName = typ.Name
            variant = Variant.FromType(typ)
            genericTypeArgs = 
                if typ.IsGenericType then
                    [ for arg in typ.GenericTypeArguments -> generateTypePattern arg ]
                else []
        }

    let rec generateStructuralType (typ: Type) : FieldType<Schema> list =
        let variant = Variant.FromType(typ)
        match variant with
        | Variant.Record ->
            let fields = FSharpType.GetRecordFields(typ, true) 
            [ for prop in fields -> { name = prop.Name; schema = generateSchema prop.PropertyType } ]
        | Variant.Tuple -> 
            FSharpType.GetTupleElements(typ)
            |> Seq.mapi (fun idx e -> 
                { name = sprintf "#%d" (idx + 1); schema = generateSchema e })
            |> List.ofSeq
        | Variant.Collection -> 
            let argTyp = 
                if typ.IsGenericType then
                    Some <| typ.GenericTypeArguments.[0]
                elif typ.IsArray then
                    Some <| typ.GetElementType()
                else    
                    None
                    
            match argTyp with
            | Some t -> generateStructuralType t
            | None -> []
        | _ -> []

    and generateSchema (typ: Type) : Schema =         
        {
            fullTypeName = typ.FullName
            structuralType = generateStructuralType typ               
            typePattern = generateTypePattern typ
        }