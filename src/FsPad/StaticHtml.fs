namespace FsPad

open System.IO
open FsHtml
open Representation

module StaticHtml = 

    let staticHeader =         
        let path = __SOURCE_DIRECTORY__ + @"\linqpadstyle.html"
        File.ReadAllText(path)

    let encode = string >> System.Net.WebUtility.HtmlEncode

    let tryField name (fields: FieldValue<_> list) = 
        fields 
        |> List.tryFind (fun (fld: FieldValue<_>) -> fld.name = name)
        |> Option.map (fun x -> x.value)

    let field name fields = 
        tryField name fields 
        |> Option.get

    module Template = 
        
        let primitive (value: Primitive) = Text (encode value)
        
        let collapsibleHeader (cols: int) (name: string) =
            th [
                yield "class" %= "collapse-trigger"
                yield "colspan" %= (string cols)
                yield span [ "class" %= "arrow-d"; Text " " ]
                yield h3 %(encode name)
            ]

        let collapsibleHeaderFromSchema (schema: Schema) = 
            collapsibleHeader schema.structuralType.Length schema.DisplayName

        let record recur (schema: Schema) (fields: FieldValue<_> list) = 
            table [
                thead [
                    collapsibleHeader 2 schema.DisplayName
                ]
                tbody [
                    for field in fields ->
                        tr [
                            td [ 
                                yield "class" %= "field"
                                yield! %(field.name) 
                            ]
                            td [ recur field.value ]
                        ]
                ]
            ]

        let listLayout recur (schema: Schema) values = 
            table [
                thead [
                    collapsibleHeaderFromSchema schema
                ]
                tbody [
                    for value in values -> 
                        tr [
                            td [ recur value ] 
                        ]
                ]
                tfoot [
                    td [
                        Text (sprintf "Length: %d" (List.length values))
                    ]
                ]
            ]

        let tableRowFromFields recur (tableSchema: Schema) (fields: FieldValue<_> list) = 
            let valueMap = 
                fields 
                |> Seq.map (fun fld -> fld.name, fld.value)
                |> Map.ofSeq
            tr [
                for field in tableSchema.structuralType ->
                    td [
                        match valueMap |> Map.tryFind field.name with
                        | Some node -> yield recur node
                        | None -> yield Text "-"
                    ]
            ]

        let tableRow recur (tableSchema: Schema) (node: TypedNode) = 
            match node with
            | Mapping (_, fields) -> tableRowFromFields recur tableSchema fields                  
            | _ -> failwith "not a row" 

        let tableLayout recur (schema: Schema) (values: TypedNode list) = 
            table [
                thead [
                    collapsibleHeaderFromSchema schema
                    tr [
                        for column in schema.structuralType ->
                            th %(column.name)
                    ]
                ]
                tbody [
                    for value in values -> 
                        tableRow recur schema value
                ]
                tfoot [
                    td [
                        "colspan" %= (string <| List.length schema.structuralType)
                        Text (sprintf "Length: %d" (List.length values))
                    ]
                ]
            ]  
            
        let tuple recur (schema: Schema) (fields: FieldValue<_> list) = 
            table [
                thead [
                    collapsibleHeaderFromSchema schema
                    tr [
                        for column in schema.structuralType ->
                            th %(column.name)
                    ]
                ]
                tbody [
                    tableRowFromFields recur schema fields
                ]
            ]   

        let union recur fields = 
            let caseName = 
                match field "Case" fields with
                | Scalar (_, value) -> unbox<string> value
                | _ -> failwith "not a union"

            let args = 
                match field "Args" fields with
                | Sequence (schema, values) -> values
                | _ -> failwith "not a union"

            match List.length args with
            | 0 -> Text ("| " + caseName)
            | n -> 
                table [
                    thead [
                        th [
                            yield "colspan" %= string n
                            yield! %("| " + caseName)
                        ]
                    ]
                    tbody [
                        tr [ 
                            for arg in args ->
                                td [ recur arg]
                        ]                            
                    ]
                ]

        let option recur fields = 
            let caseName = 
                match field "Case" fields with
                | Scalar (_, value) -> unbox<string> value
                | _ -> "None"

            let args = 
                match field "Args" fields with
                | Sequence (_, values) -> values
                | _ -> []

            match caseName, args with
            | "Some", [ arg ] -> recur arg
            | _, _ -> Text "-"

        let chunk () = Text "..."
            
    let rec render (node: TypedNode) = 
        match node with
        // a simple value
        | Scalar (schema, value) -> Template.primitive value
        // option special case
        | Mapping (({ typePattern = { typeName = "FSharpOption`1"; variant = Variant.Union }} as schema), fields) -> 
            Template.option render fields                
        | Mapping (({ typePattern = { variant = Variant.Union }} as schema), fields) -> 
            Template.union render fields                
        // a tuple
        | Mapping (({ typePattern = { variant = Variant.Tuple }} as schema), fields) -> 
            Template.tuple render schema fields
        // a simple record
        | Mapping (schema, fields) -> Template.record render schema fields
        // a collection of things
        | Sequence (schema, values) -> 
            if List.length values > 1 && (not <| List.isEmpty schema.structuralType) then
                Template.tableLayout render schema values
            else
                Template.listLayout render schema values
        // lazy values or max recurse level reached - we don't really handle them for now.
        | Chunk (schema, _) -> Template.chunk ()

    let renderWithStaticHeader (node: TypedNode) = 
        let content = render node
        html [
            head %(staticHeader)
            body [
                div [
                    yield "class" %= "spacer"
                    yield! %(content)
                ]
            ]
        ]
