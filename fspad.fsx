﻿//  Imported fsx from gusty's prototype

//  https://gist.githubusercontent.com/gusty/96f4b36780f1fcf2200252ac1ce05074/raw/7404dbb696885a7491e54c25aadf1a201754dfcd/FsPad.fsx

open System
open System.Collections.Generic
open System.Runtime.Serialization
open System.Reflection
open Microsoft.FSharp.Reflection

//------------------------------------
// Section: TypeShape core definitions

/// Provides a simple breakdown of basic kinds of types.
/// Used for easier extraction of type shapes in the active pattern implementations.
[<NoEquality; NoComparison>]
type TypeShapeInfo =
    | Basic of Type
    | Enum of enumTy:Type * underlying:Type
    | Array of element:Type * rank:int
    | Generic of definition:Type * args:Type []

/// Used to extract the type variable contained in a specific shape
type ITypeShapeVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

/// Encapsulates a type variable that can be accessed using type shape visitors
[<AbstractClass>]
type TypeShape =
    [<CompilerMessage("TypeShape constructor should not be consumed.", 4224)>]
    internal new () = { }
    abstract Type : Type
    abstract ShapeInfo : TypeShapeInfo
    abstract Accept : ITypeShapeVisitor<'R> -> 'R
    override s.ToString() = sprintf "TypeShape [%O]" s.Type

/// Encapsulates a type variable that can be accessed using type shape visitors
[<Sealed>]
type TypeShape<'T> () =
    inherit TypeShape()
    static let shapeInfo =
        let t = typeof<'T>
        if t.IsEnum then
            Enum(t, Enum.GetUnderlyingType t)
        elif t.IsArray then
            Array(t.GetElementType(), t.GetArrayRank())
        elif t.IsGenericType then 
            Generic(t.GetGenericTypeDefinition(), t.GetGenericArguments())
        else
            Basic t
        
    override __.Type = typeof<'T>
    override __.ShapeInfo = shapeInfo
    override __.Accept v = v.Visit<'T> ()
    override __.Equals o = o :? TypeShape<'T>
    override __.GetHashCode() = hash typeof<'T>

exception UnsupportedShape of Type:Type
    with
    override __.Message = sprintf "Unsupported TypeShape '%O'" __.Type

[<AutoOpen>]
module private TypeShapeImpl =

    let fsharpCoreRuntimeVersion =
        typeof<unit>.Assembly.GetName().Version

    let fsharpCore41Version = Version(4,4,1,0)

    let allMembers =
        BindingFlags.NonPublic ||| BindingFlags.Public |||
            BindingFlags.Instance ||| BindingFlags.Static |||
                BindingFlags.FlattenHierarchy 

    let allInstanceMembers =
        BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Instance

    type MemberInfo with
        member inline m.ContainsAttr<'Attr when 'Attr :> Attribute> (inheritAttr) =
            m.GetCustomAttributes(inheritAttr)
            |> Array.exists (function :? 'Attr -> true | _ -> false)

        member inline m.TryGetAttribute<'Attr when 'Attr :> Attribute> (inheritAttr) =
            m.GetCustomAttributes(inheritAttr)
            |> Array.tryPick (function :? 'Attr as attr -> Some attr | _ -> None)

    let activateGeneric (templateTy:Type) (typeArgs : Type[]) (args:obj[]) =
        let templateTy =
            if typeArgs.Length = 0 then templateTy
            elif not templateTy.IsGenericType then invalidArg (string templateTy) "not generic."
            elif not templateTy.IsGenericTypeDefinition then
                templateTy.GetGenericTypeDefinition().MakeGenericType typeArgs
            else
                templateTy.MakeGenericType typeArgs

        let ctypes = args |> Array.map (fun o -> o.GetType())
        let ctor = templateTy.GetConstructor(allMembers, null, CallingConventions.Standard, ctypes, [||])
        ctor.Invoke args

    /// correctly resolves if type is assignable to interface
    let rec isInterfaceAssignableFrom (iface : Type) (ty : Type) =
        let proj (t : Type) = t.Assembly, t.Namespace, t.Name, t.MetadataToken
        if iface = ty then true
        elif ty.GetInterfaces() |> Array.exists(fun if0 -> proj if0 = proj iface) then true
        else
            match ty.BaseType with
            | null -> false
            | bt -> isInterfaceAssignableFrom iface bt

    let private canon = Type.GetType "System.__Canon"
    let private genShapeTy = typedefof<TypeShape<_>>

    let resolveTypeShape(typ : Type) =
        if typ = null then raise <| ArgumentNullException("TypeShape: System.Type cannot be null.")
        if typ.IsGenericTypeDefinition then raise <| UnsupportedShape typ
        elif typ.IsGenericParameter then raise <| UnsupportedShape typ
        elif typ = canon then raise <| UnsupportedShape typ
        elif typ.IsByRef || typ.IsPointer then raise <| UnsupportedShape typ
        else 
            let gt = genShapeTy.MakeGenericType [|typ|]
            Activator.CreateInstance gt :?> TypeShape

type Activator with
    /// Generic edition of the activator method which support type parameters and private types
    static member CreateInstanceGeneric<'Template>(?typeArgs : Type[], ?args:obj[]) : obj =
        let typeArgs = defaultArg typeArgs [||]
        let args = defaultArg args [||]
        activateGeneric typeof<'Template> typeArgs args

type Type with
    /// Correctly resolves if type is assignable to interface
    member iface.IsInterfaceAssignableFrom(ty : Type) : bool =
        isInterfaceAssignableFrom iface ty

type TypeShape with
    /// <summary>
    ///     Creates a type shape instance for given type
    /// </summary>
    /// <param name="typ">System.Type to be resolved.</param>
    static member Create(typ : Type) : TypeShape = resolveTypeShape typ

    /// <summary>
    ///     Creates a type shape instance from the underlying
    ///     type of a given value.
    /// </summary>
    /// <param name="obj">Non-null value to extract shape data from.</param>
    static member FromValue(obj : obj) : TypeShape =
        match obj with
        | null -> raise <| ArgumentNullException()
        | obj -> resolveTypeShape (obj.GetType())

    /// <summary>
    ///     Creates a type shape instance for given type
    /// </summary>
    static member Create<'T>() : TypeShape<'T> = new TypeShape<'T>()

/// Creates a type shape instance for given type
let shapeof<'T> = TypeShape.Create<'T>() :> TypeShape

/// Typed variation of the shapeof operator
let tshapeof<'T> = TypeShape.Create<'T>()

//------------------------
// Section: Core BCL types

// Enum types

type IEnumVisitor<'R> =
    abstract Visit<'Enum, 'Underlying when 'Enum : enum<'Underlying>
                                       and 'Enum : struct
                                       and 'Enum :> ValueType
                                       and 'Enum : (new : unit -> 'Enum)> : unit -> 'R

type IShapeEnum =
    abstract Underlying : TypeShape
    abstract Accept : IEnumVisitor<'R> -> 'R

type private ShapeEnum<'Enum, 'Underlying when 'Enum : enum<'Underlying>
                                           and 'Enum : struct
                                           and 'Enum :> ValueType
                                           and 'Enum : (new : unit -> 'Enum)>() =
    interface IShapeEnum with
        member __.Underlying = shapeof<'Underlying>
        member __.Accept v = v.Visit<'Enum, 'Underlying> ()

// Nullable types

type INullableVisitor<'R> =
    abstract Visit<'T when 'T : (new : unit -> 'T) and 'T :> ValueType and 'T : struct> : unit -> 'R

type IShapeNullable =
    abstract Element : TypeShape
    abstract Accept : INullableVisitor<'R> -> 'R

type private ShapeNullable<'T when 'T : (new : unit -> 'T) and 'T :> ValueType and 'T : struct> () =
    interface IShapeNullable with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()


// Default Constructor types

type IDefaultConstructorVisitor<'R> =
    abstract Visit<'T when 'T : (new : unit -> 'T)> : unit -> 'R

type IShapeDefaultConstructor =
    abstract Accept : IDefaultConstructorVisitor<'R> -> 'R

type private ShapeDefaultConstructor<'T when 'T : (new : unit -> 'T)>() =
    interface IShapeDefaultConstructor with
        member __.Accept v = v.Visit<'T>()

// Equality Types
    
type IEqualityVisitor<'R> =
    abstract Visit<'T when 'T : equality> : unit -> 'R

type IShapeEquality =
    abstract Accept : IEqualityVisitor<'R> -> 'R

type private ShapeEquality<'T when 'T : equality>() =
    interface IShapeEquality with
        member __.Accept v = v.Visit<'T>()

// Comparison Types
    
type IComparisonVisitor<'R> =
    abstract Visit<'T when 'T : comparison> : unit -> 'R

type IShapeComparison =
    abstract Accept : IComparisonVisitor<'R> -> 'R

type private ShapeComparison<'T when 'T : comparison>() =
    interface IShapeComparison with
        member __.Accept v = v.Visit<'T>()

// Struct Types

type IStructVisitor<'R> =
    abstract Visit<'T when 'T : struct> : unit -> 'R

type IShapeStruct =
    abstract Accept : IStructVisitor<'R> -> 'R

type private ShapeStruct<'T when 'T : struct>() =
    interface IShapeStruct with
        member __.Accept v = v.Visit<'T>()

// Reference Types

type INotStructVisitor<'R> =
    abstract Visit<'T when 'T : not struct and 'T : null> : unit -> 'R

type IShapeNotStruct =
    abstract Accept : INotStructVisitor<'R> -> 'R

type private ShapeNotStruct<'T when 'T : not struct and 'T : null>() =
    interface IShapeNotStruct with
        member __.Accept v = v.Visit<'T>()

// Delegates

type IDelegateVisitor<'R> =
    abstract Visit<'Delegate when 'Delegate :> Delegate> : unit -> 'R

type IShapeDelegate =
    abstract Accept : IDelegateVisitor<'R> -> 'R

type private ShapeDelegate<'Delegate when 'Delegate :> Delegate>() =
    interface IShapeDelegate with
        member __.Accept v = v.Visit<'Delegate>()

// System.Tuple`1

type ITuple1Visitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeTuple1 =
    abstract Item1 : TypeShape
    abstract Accept : ITuple1Visitor<'R> -> 'R

type private ShapeTuple1<'T> () =
    interface IShapeTuple1 with
        member __.Item1 = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()

// System.Tuple`2

type ITuple2Visitor<'R> =
    abstract Visit<'T1, 'T2> : unit -> 'R

type IShapeTuple2 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Accept : ITuple2Visitor<'R> -> 'R

type private ShapeTuple2<'T1, 'T2> () =
    interface IShapeTuple2 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Accept v = v.Visit<'T1,'T2> ()

// System.Tuple`3

type ITuple3Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3> : unit -> 'R

type IShapeTuple3 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Item3 : TypeShape
    abstract Accept : ITuple3Visitor<'R> -> 'R

type private ShapeTuple3<'T1, 'T2, 'T3> () =
    interface IShapeTuple3 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Item3 = shapeof<'T3>
        member __.Accept v = v.Visit<'T1, 'T2, 'T3> ()

// System.Tuple`4

type ITuple4Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4> : unit -> 'R

type IShapeTuple4 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Item3 : TypeShape
    abstract Item4 : TypeShape
    abstract Accept : ITuple4Visitor<'R> -> 'R

type private ShapeTuple4<'T1, 'T2, 'T3, 'T4> () =
    interface IShapeTuple4 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Item3 = shapeof<'T3>
        member __.Item4 = shapeof<'T4>
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4> ()

// System.Tuple`5

type ITuple5Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5> : unit -> 'R

type IShapeTuple5 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Item3 : TypeShape
    abstract Item4 : TypeShape
    abstract Item5 : TypeShape
    abstract Accept : ITuple5Visitor<'R> -> 'R

type private ShapeTuple5<'T1, 'T2, 'T3, 'T4, 'T5> () =
    interface IShapeTuple5 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Item3 = shapeof<'T3>
        member __.Item4 = shapeof<'T4>
        member __.Item5 = shapeof<'T5>
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5> ()

// System.Tuple`6

type ITuple6Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> : unit -> 'R

type IShapeTuple6 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Item3 : TypeShape
    abstract Item4 : TypeShape
    abstract Item5 : TypeShape
    abstract Item6 : TypeShape
    abstract Accept : ITuple6Visitor<'R> -> 'R

type private ShapeTuple6<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> () =
    interface IShapeTuple6 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Item3 = shapeof<'T3>
        member __.Item4 = shapeof<'T4>
        member __.Item5 = shapeof<'T5>
        member __.Item6 = shapeof<'T6>
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> ()

// System.Tuple`7

type ITuple7Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> : unit -> 'R

type IShapeTuple7 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Item3 : TypeShape
    abstract Item4 : TypeShape
    abstract Item5 : TypeShape
    abstract Item6 : TypeShape
    abstract Item7 : TypeShape
    abstract Accept : ITuple7Visitor<'R> -> 'R

type private ShapeTuple7<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> () =
    interface IShapeTuple7 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Item3 = shapeof<'T3>
        member __.Item4 = shapeof<'T4>
        member __.Item5 = shapeof<'T5>
        member __.Item6 = shapeof<'T6>
        member __.Item7 = shapeof<'T7>
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> ()

// System.Tuple`8

type ITuple8Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7, 'TRest> : unit -> 'R

type IShapeTuple8 =
    abstract Item1 : TypeShape
    abstract Item2 : TypeShape
    abstract Item3 : TypeShape
    abstract Item4 : TypeShape
    abstract Item5 : TypeShape
    abstract Item6 : TypeShape
    abstract Item7 : TypeShape
    abstract Rest : TypeShape
    abstract Accept : ITuple8Visitor<'R> -> 'R

type private ShapeTuple8<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7, 'TRest> () =
    interface IShapeTuple8 with
        member __.Item1 = shapeof<'T1>
        member __.Item2 = shapeof<'T2>
        member __.Item3 = shapeof<'T3>
        member __.Item4 = shapeof<'T4>
        member __.Item5 = shapeof<'T5>
        member __.Item6 = shapeof<'T6>
        member __.Item7 = shapeof<'T7>
        member __.Rest = shapeof<'TRest>
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7, 'TRest> ()

// F# functions

type IFSharpFuncVisitor<'R> =
    abstract Visit<'Domain, 'CoDomain> : unit -> 'R

type IShapeFSharpFunc =
    abstract Domain : TypeShape
    abstract CoDomain : TypeShape
    abstract Accept : IFSharpFuncVisitor<'R> -> 'R

type private ShapeFSharpFunc<'Domain, 'CoDomain> () =
    interface IShapeFSharpFunc with
        member __.Domain = shapeof<'Domain>
        member __.CoDomain = shapeof<'CoDomain>
        member __.Accept v = v.Visit<'Domain, 'CoDomain> ()

// System.Exception

type IExceptionVisitor<'R> =
    abstract Visit<'exn when 'exn :> exn and 'exn : not struct and 'exn : null> : unit -> 'R

type IShapeException =
    abstract IsFSharpException : bool
    abstract Accept : IExceptionVisitor<'R> -> 'R

type private ShapeException<'exn when 'exn :> exn and 'exn : not struct and 'exn : null> (isFSharpExn : bool) =
    interface IShapeException with
        member __.IsFSharpException = isFSharpExn
        member __.Accept v = v.Visit<'exn> ()


//-----------------------------------
// Section: Collections & IEnumerable

// IEnumerable

type IEnumerableVisitor<'R> =
    abstract Visit<'Enum, 'T when 'Enum :> seq<'T>> : unit -> 'R

type IShapeEnumerable =
    abstract Element : TypeShape
    abstract Accept : IEnumerableVisitor<'R> -> 'R

type private ShapeEnumerable<'Enum, 'T when 'Enum :> seq<'T>> () =
    interface IShapeEnumerable with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'Enum, 'T> ()

// Collection

type ICollectionVisitor<'R> =
    abstract Visit<'Collection, 'T when 'Collection :> ICollection<'T>> : unit -> 'R

type IShapeCollection =
    abstract Element : TypeShape
    abstract Accept : ICollectionVisitor<'R> -> 'R

type private ShapeCollection<'Collection, 'T when 'Collection :> ICollection<'T>> () =
    interface IShapeCollection with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'Collection, 'T> ()

// KeyValuePair

type IKeyValuePairVisitor<'R> =
    abstract Visit<'K, 'V> : unit -> 'R

type IShapeKeyValuePair =
    abstract Key : TypeShape
    abstract Value : TypeShape
    abstract Accept : IKeyValuePairVisitor<'R> -> 'R

type private ShapeKeyValuePair<'K,'V> () =
    interface IShapeKeyValuePair with
        member __.Key = shapeof<'K>
        member __.Value = shapeof<'V>
        member __.Accept v = v.Visit<'K, 'V> ()

// System.Array

type IArrayVisitor<'R> =
    abstract Visit<'T> : rank:int -> 'R

type IShapeArray =
    /// Gets the rank of the array type shape
    abstract Rank : int
    abstract Element : TypeShape
    abstract Accept : IArrayVisitor<'R> -> 'R

type private ShapeArray<'T>(rank : int) =
    /// Gets the rank of the array type shape
    member __.Rank = rank
    interface IShapeArray with
        member __.Rank = rank
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> rank

type ISystemArrayVisitor<'R> =
    abstract Visit<'Array when 'Array :> System.Array> : unit -> 'R

type IShapeSystemArray =
    abstract Rank : int
    abstract Element : TypeShape
    abstract Accept : ISystemArrayVisitor<'R> -> 'R

type private ShapeSystemArray<'Array when 'Array :> System.Array>(elem : Type, rank : int) =
    interface IShapeSystemArray with
        member __.Rank = rank
        member __.Element = TypeShape.Create elem
        member __.Accept v = v.Visit<'Array> ()
    

// System.Collections.List

type IResizeArrayVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeResizeArray =
    abstract Element : TypeShape
    abstract Accept : IResizeArrayVisitor<'R> -> 'R

type private ShapeResizeArray<'T> () =
    interface IShapeResizeArray with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()


// System.Collections.Dictionary

type IDictionaryVisitor<'R> =
    abstract Visit<'K, 'V when 'K : equality> : unit -> 'R

type IShapeDictionary =
    abstract Key : TypeShape
    abstract Value : TypeShape
    abstract Accept : IDictionaryVisitor<'R> -> 'R

type private ShapeDictionary<'K, 'V when 'K : equality> () =
    interface IShapeDictionary with
        member __.Key = shapeof<'K>
        member __.Value = shapeof<'V>
        member __.Accept v = v.Visit<'K, 'V> ()

// System.Collections.HashSet

type IHashSetVisitor<'R> =
    abstract Visit<'T when 'T : equality> : unit -> 'R

type IShapeHashSet =
    abstract Element : TypeShape
    abstract Accept : IHashSetVisitor<'R> -> 'R

type private ShapeHashSet<'T when 'T : equality> () =
    interface IShapeHashSet with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()

// F# Set

type IFSharpSetVisitor<'R> =
    abstract Visit<'T when 'T : comparison> : unit -> 'R

type IShapeFSharpSet =
    abstract Element : TypeShape
    abstract Accept : IFSharpSetVisitor<'R> -> 'R

type private ShapeFSharpSet<'T when 'T : comparison> () =
    interface IShapeFSharpSet with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()

// F# Map

type IFSharpMapVisitor<'R> =
    abstract Visit<'K, 'V when 'K : comparison> : unit -> 'R

type IShapeFSharpMap =
    abstract Key : TypeShape
    abstract Value : TypeShape
    abstract Accept : IFSharpMapVisitor<'R> -> 'R

type private ShapeFSharpMap<'K, 'V when 'K : comparison> () =
    interface IShapeFSharpMap with
        member __.Key = shapeof<'K>
        member __.Value = shapeof<'V>
        member __.Accept v = v.Visit<'K, 'V>()

// F# ref

type IShapeFSharpRef =
    abstract Element : TypeShape
    abstract Accept : IFSharpRefVisitor<'R> -> 'R

and IFSharpRefVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type private ShapeFSharpRef<'T> () =
    interface IShapeFSharpRef with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()

// F# option

type IFSharpOptionVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeFSharpOption =
    abstract Element : TypeShape
    abstract Accept : IFSharpOptionVisitor<'R> -> 'R

type private ShapeFSharpOption<'T> () =
    interface IShapeFSharpOption with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()

// F# List

type IFSharpListVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeFSharpList =
    abstract Element : TypeShape
    abstract Accept : IFSharpListVisitor<'R> -> 'R

type private ShapeFSharpList<'T> () =
    interface IShapeFSharpList with
        member __.Element = shapeof<'T>
        member __.Accept v = v.Visit<'T> ()

// F# Choice`2

type IFSharpChoice2Visitor<'R> =
    abstract Visit<'T1,'T2> : unit -> 'R

type IShapeFSharpChoice2 =
    abstract Type1 : TypeShape
    abstract Type2 : TypeShape
    abstract Accept : IFSharpChoice2Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2> () =
    interface IShapeFSharpChoice2 with
        member __.Type1 = shapeof<'T1>
        member __.Type2 = shapeof<'T2>
        member __.Accept v = v.Visit<'T1,'T2>()

// F# Choice`3

type IFSharpChoice3Visitor<'R> =
    abstract Visit<'T1,'T2,'T3> : unit -> 'R

type IShapeFSharpChoice3 =
    abstract Type1 : TypeShape
    abstract Type2 : TypeShape
    abstract Type3 : TypeShape
    abstract Accept : IFSharpChoice3Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3> () =
    interface IShapeFSharpChoice3 with
        member __.Type1 = shapeof<'T1>
        member __.Type2 = shapeof<'T2>
        member __.Type3 = shapeof<'T3>
        member __.Accept v = v.Visit<'T1,'T2,'T3>()

// F# Choice`4

type IFSharpChoice4Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4> : unit -> 'R

type IShapeFSharpChoice4 =
    abstract Type1 : TypeShape
    abstract Type2 : TypeShape
    abstract Type3 : TypeShape
    abstract Type4 : TypeShape
    abstract Accept : IFSharpChoice4Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4> () =
    interface IShapeFSharpChoice4 with
        member __.Type1 = shapeof<'T1>
        member __.Type2 = shapeof<'T2>
        member __.Type3 = shapeof<'T3>
        member __.Type4 = shapeof<'T4>
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4>()

// F# Choice`5

type IFSharpChoice5Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4,'T5> : unit -> 'R

type IShapeFSharpChoice5 =
    abstract Type1 : TypeShape
    abstract Type2 : TypeShape
    abstract Type3 : TypeShape
    abstract Type4 : TypeShape
    abstract Type5 : TypeShape
    abstract Accept : IFSharpChoice5Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4, 'T5> () =
    interface IShapeFSharpChoice5 with
        member __.Type1 = shapeof<'T1>
        member __.Type2 = shapeof<'T2>
        member __.Type3 = shapeof<'T3>
        member __.Type4 = shapeof<'T4>
        member __.Type5 = shapeof<'T5>
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4,'T5>()

// F# Choice`6

type IFSharpChoice6Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4,'T5,'T6> : unit -> 'R

type IShapeFSharpChoice6 =
    abstract Type1 : TypeShape
    abstract Type2 : TypeShape
    abstract Type3 : TypeShape
    abstract Type4 : TypeShape
    abstract Type5 : TypeShape
    abstract Type6 : TypeShape
    abstract Accept : IFSharpChoice6Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> () =
    interface IShapeFSharpChoice6 with
        member __.Type1 = shapeof<'T1>
        member __.Type2 = shapeof<'T2>
        member __.Type3 = shapeof<'T3>
        member __.Type4 = shapeof<'T4>
        member __.Type5 = shapeof<'T5>
        member __.Type6 = shapeof<'T6>
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4,'T5,'T6>()

// F# Choice`7

type IFSharpChoice7Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4,'T5,'T6,'T7> : unit -> 'R

type IShapeFSharpChoice7 =
    abstract Type1 : TypeShape
    abstract Type2 : TypeShape
    abstract Type3 : TypeShape
    abstract Type4 : TypeShape
    abstract Type5 : TypeShape
    abstract Type6 : TypeShape
    abstract Type7 : TypeShape
    abstract Accept : IFSharpChoice7Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> () =
    interface IShapeFSharpChoice7 with
        member __.Type1 = shapeof<'T1>
        member __.Type2 = shapeof<'T2>
        member __.Type3 = shapeof<'T3>
        member __.Type4 = shapeof<'T4>
        member __.Type5 = shapeof<'T5>
        member __.Type6 = shapeof<'T6>
        member __.Type7 = shapeof<'T7>
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4,'T5,'T6,'T7>()

//-----------------------------
// Section: Member-based Shapes

[<AutoOpen>]
module private MemberUtils =

    let defaultOfUntyped (ty : Type) =
        TypeShape.Create(ty).Accept {
            new ITypeShapeVisitor<obj> with
                member __.Visit<'T>() = Unchecked.defaultof<'T> :> obj
        }

    let inline invalidMember (memberInfo : MemberInfo) =
        sprintf "TypeShape internal error: invalid MemberInfo '%O'" memberInfo
        |> invalidOp

    let isStructMember (path : MemberInfo[]) =
        path |> Array.exists (fun m -> m.DeclaringType.IsValueType)

    let isPublicMember (memberInfo : MemberInfo) =
        match memberInfo with
        | :? FieldInfo as f -> f.IsPublic
        | :? PropertyInfo as p -> p.GetGetMethod(true).IsPublic
        | _ -> invalidMember memberInfo

      
    let isWriteableMember (path : MemberInfo[]) =
        path 
        |> Array.forall (fun m ->
            match m with
            | :? FieldInfo -> true
            | :? PropertyInfo as p -> p.CanWrite
            | _ -> invalidMember m)

    let inline getValue (obj:obj) (m:MemberInfo) =
        match m with
        | :? FieldInfo as f -> f.GetValue(obj)
        | :? PropertyInfo as p -> p.GetValue(obj, null)
        | _ -> invalidMember m

    let inline setValue (obj:obj) (m:MemberInfo) (value:obj) =
        match m with
        | :? FieldInfo as f -> f.SetValue(obj, value)
        | :? PropertyInfo as p -> p.SetValue(obj, value, null)
        | _ -> invalidMember m

    let inline project<'Record, 'Member> (path : MemberInfo[]) (value:'Record) =
        let mutable obj = box value
        for m in path do
            obj <- getValue obj m

        obj :?> 'Member

    let inline inject<'Record, 'Member> (isStructMember : bool) (path : MemberInfo[]) 
                                        (r : 'Record) (value : 'Member) =
        let mutable obj = box r
        let n = path.Length
        if isStructMember then
            let valueStack = Array.zeroCreate<obj> (n - 1)
            for i = 0 to n - 2 do
                valueStack.[i] <- obj
                obj <- getValue obj path.[i]

            setValue obj path.[n - 1] value
            for i = n - 2 downto 0 do
                let obj2 = valueStack.[i]
                setValue obj2 path.[i] obj
                obj <- obj2

            obj :?> 'Record          
        else
            for i = 0 to n - 2 do
                obj <- getValue obj path.[i]

            setValue obj path.[n - 1] value
            r


                

//-------------------------
// Member Shape Definitions

/// Identifies an instance member that defines
/// a value in a class instance, typically a field or property
type IShapeMember =
    /// Human-readable member identifier
    abstract Label : string
    /// The actual System.Reflection.MemberInfo corresponding to member
    abstract MemberInfo : MemberInfo
    /// Type of value stored by member
    abstract Member : TypeShape
    /// True iff member is contained within a struct
    abstract IsStructMember : bool
    /// True iff member is public
    abstract IsPublic : bool

/// Identifies an instance member that defines
/// a value in a class instance, typically a field or property
type IShapeMember<'DeclaringType> =
    inherit IShapeMember
    abstract Accept : IMemberVisitor<'DeclaringType, 'R> -> 'R

/// Identifies an instance member that defines
/// a value in a class instance, typically a field or property
and ShapeMember<'DeclaringType, 'MemberType> internal (label : string, memberInfo : MemberInfo, path : MemberInfo[]) =
    let isStructMember = isStructMember path
    let isPublicMember = isPublicMember memberInfo
#if TYPESHAPE_EMIT
    let projectFunc = emitProjection<'DeclaringType, 'MemberType> path
#endif

    /// Human-readable member identifier
    member __.Label = label
    /// The actual System.Reflection.MemberInfo corresponding to member
    member __.MemberInfo = memberInfo
    /// True iff member is contained within a struct
    member __.IsStructMember = isStructMember
    /// True iff member is public
    member __.IsPublic = isPublicMember
    /// Projects an instance to member of given value
    member __.Project (instance : 'DeclaringType) : 'MemberType =
#if TYPESHAPE_EMIT
        projectFunc.Value.Invoke instance
#else
        project path instance
#endif
       
#if TYPESHAPE_EXPR
    /// Projects an instance to member of given value
    member __.ProjectExpr (instance : Expr<'DeclaringType>) =
        projectExpr<'DeclaringType, 'MemberType> path instance
#endif

    interface IShapeMember<'DeclaringType> with
        member s.Label = label
        member s.Member = shapeof<'MemberType>
        member s.MemberInfo = memberInfo
        member s.IsStructMember = isStructMember
        member s.IsPublic = isPublicMember
        member s.Accept v = v.Visit s

and IMemberVisitor<'DeclaringType, 'R> =
    abstract Visit<'MemberType> : ShapeMember<'DeclaringType, 'MemberType> -> 'R

//----------------------------
// Writable Member Definitions

/// Identifies an instance member that defines
/// a mutable value in a class instance, typically a field or property
type IShapeWriteMember<'Record> =
    inherit IShapeMember<'Record>
    abstract Accept : IWriteMemberVisitor<'Record,'R> -> 'R

/// Identifies an instance member that defines
/// a mutable value in a class instance, typically a field or property
and [<Sealed>] ShapeWriteMember<'DeclaringType, 'MemberType> private (label : string, memberInfo : MemberInfo, path : MemberInfo[]) =
    inherit ShapeMember<'DeclaringType, 'MemberType>(label, memberInfo, path)

#if TYPESHAPE_EMIT
    let injectFunc = emitInjection<'DeclaringType, 'MemberType> path
#else
    let isStructMember = isStructMember path
#endif

    /// Injects a value to member of given instance
    member __.Inject (instance : 'DeclaringType) (field : 'MemberType) : 'DeclaringType =
#if TYPESHAPE_EMIT
        injectFunc.Value.Invoke(instance, field)
#else
        inject isStructMember path instance field
#endif

#if TYPESHAPE_EXPR

    /// Injects a value to member of given instance
    member __.InjectExpr (instance : Expr<'DeclaringType>) (field : Expr<'MemberType>) =
        injectExpr path instance field
#endif

    interface IShapeWriteMember<'DeclaringType> with
        member s.Accept (v : IWriteMemberVisitor<'DeclaringType, 'R>) = v.Visit s

and IWriteMemberVisitor<'TRecord, 'R> =
    abstract Visit<'Field> : ShapeWriteMember<'TRecord, 'Field> -> 'R

//-------------------------------
// Constructor Shapes

/// Identifies a constructor implementation shape
type IShapeConstructor =
    /// Denotes whether constructor is public
    abstract IsPublic : bool
    /// Denotes the arity of the constructor arguments
    abstract Arity : int
    /// ConstructorInfo instance
    abstract ConstructorInfo : ConstructorInfo
    // A tuple type encoding all arguments passed to the constuctor
    abstract Arguments : TypeShape

/// Identifies a constructor implementation shape
and IShapeConstructor<'DeclaringType> =
    inherit IShapeConstructor
    abstract Accept : IConstructorVisitor<'DeclaringType, 'R> -> 'R

/// Identifies a constructor implementation shape
and [<Sealed>] ShapeConstructor<'DeclaringType, 'CtorArgs> private (ctorInfo : ConstructorInfo, arity : int) =
    let valueReader = 
        match arity with
        | 0 -> fun _ -> [||]
        | 1 -> fun x -> [|x|]
        |_ -> FSharpValue.PreComputeTupleReader typeof<'CtorArgs>

    /// Creates an instance of declaring type with supplied constructor args
    member __.Invoke(args : 'CtorArgs) =
        let args = valueReader args
        ctorInfo.Invoke args :?> 'DeclaringType

#if TYPESHAPE_EXPR
    /// Creates an instance of declaring type with supplied constructor args
    member __.InvokeExpr(args : Expr<'CtorArgs>) : Expr<'DeclaringType> =
        let exprArgs = 
            match arity with
            | 1 -> [args :> Expr]
            | _ -> [for i in 0 .. arity - 1 -> Expr.TupleGet(args, i)]

        Expr.Cast<'DeclaringType>(Expr.NewObject(ctorInfo, exprArgs))
#endif

    interface IShapeConstructor<'DeclaringType> with
        member __.IsPublic = ctorInfo.IsPublic
        member __.Arity = arity
        member __.ConstructorInfo = ctorInfo
        member __.Arguments = shapeof<'CtorArgs>
        member __.Accept v = v.Visit __

and IConstructorVisitor<'CtorType, 'R> =
    abstract Visit<'CtorArgs> : ShapeConstructor<'CtorType, 'CtorArgs> -> 'R

//---------------------------
// Supplementary Member utils

[<AutoOpen>]
module private MemberUtils2 =
    let mkMemberUntyped<'Record> (label : string) (memberInfo : MemberInfo) (path : MemberInfo[]) =
        let memberType = 
            match path.[path.Length - 1] with
            | :? FieldInfo as fI -> fI.FieldType
            | :? PropertyInfo as pI -> pI.PropertyType
            | m -> invalidMember m

        let tyArgs = [|typeof<'Record> ; memberType|]
        let args = [|box label; box memberInfo; box path|]
        if isWriteableMember path then
            Activator.CreateInstanceGeneric<ShapeWriteMember<_,_>>(tyArgs, args)
            :?> IShapeMember<'Record>
        else
            Activator.CreateInstanceGeneric<ShapeMember<_,_>>(tyArgs, args) 
            :?> IShapeMember<'Record>
        
    let mkWriteMemberUntyped<'Record> (label : string) (memberInfo : MemberInfo) (path : MemberInfo[]) =
        match mkMemberUntyped<'Record> label memberInfo path with
        | :? IShapeWriteMember<'Record> as wm -> wm
        | _ -> invalidOp <| sprintf "TypeShape internal error: Member '%O' is not writable" memberInfo

    let mkCtorUntyped<'Record> (ctorInfo : ConstructorInfo) =
        let argTypes = ctorInfo.GetParameters() |> Array.map (fun p -> p.ParameterType)
        let arity = argTypes.Length
        let argumentType =
            match arity with
            | 0 -> typeof<unit>
            | 1 -> argTypes.[0]
            | _ -> FSharpType.MakeTupleType argTypes

        Activator.CreateInstanceGeneric<ShapeConstructor<_,_>>([|typeof<'Record>; argumentType|], [|box ctorInfo; box arity|])
        :?> IShapeConstructor<'Record>

//--------------------
// Generic Tuple Shape

[<AutoOpen>]
module private ShapeTupleImpl =

    [<NoEquality; NoComparison>]
    type TupleInfo =
        { 
            Current : Type
            Fields : (MemberInfo * FieldInfo) []
            Nested : (FieldInfo * TupleInfo) option
        }

    let rec mkTupleInfo (t : Type) =
        if t.IsValueType then
            let fields = t.GetFields()
            let getField (f : FieldInfo) = f :> MemberInfo, f
            let fs, nested =
                if fields.Length = 8 then
                    let nestedField = fields.[7]
                    let nestedInfo = mkTupleInfo nestedField.FieldType
                    Array.map getField fields.[..6], Some(nestedField, nestedInfo)
                else
                    Array.map getField fields, None

            { Current = t ; Fields = fs ; Nested = nested }
        else
            let props = t.GetProperties()
            let fields = t.GetFields(BindingFlags.NonPublic ||| BindingFlags.Instance)
            let getField (p : PropertyInfo) =
                let field = fields |> Array.find(fun f -> f.Name = "m_" + p.Name)
                p :> MemberInfo, field

            let fs, nested =
                if props.Length = 8 then
                    let nestedField = fields.[7]
                    let nestedInfo = mkTupleInfo nestedField.FieldType
                    Array.map getField props.[..6], Some(nestedField, nestedInfo)
                else
                    Array.map getField props, None

            { Current = t ; Fields = fs ; Nested = nested }

    let gatherTupleMembers (tI : TupleInfo) =
        let rec aux (ctx : MemberInfo list) (tI : TupleInfo) = seq {
            for p,f in tI.Fields do
                yield p, f :> MemberInfo :: ctx |> List.rev |> List.toArray

            match tI.Nested with
            | Some (fI,n) -> yield! aux (fI :> MemberInfo :: ctx) n
            | None -> ()
        }

        aux [] tI

    let gatherNestedFields (tI : TupleInfo) =
        let rec aux fs (tI : TupleInfo) =
            match tI.Nested with
            | Some (fI,n) -> aux (fI :: fs) n
            | _ -> List.rev fs

        aux [] tI
        |> List.toArray

//---------------------------
// Shape Tuple Implementation

/// Denotes a specific System.Tuple shape
type IShapeTuple =
    /// Tuple element shape definitions
    abstract Elements : IShapeMember[]
    abstract Accept : ITupleVisitor<'R> -> 'R

and ITupleVisitor<'R> =
    abstract Visit : ShapeTuple<'Tuple> -> 'R 

/// Identifies a specific System.Tuple shape
and [<Sealed>] ShapeTuple<'Tuple> private () =
    let tupleInfo = mkTupleInfo typeof<'Tuple>
    let isStructTuple = typeof<'Tuple>.IsValueType

    let tupleElems =
        gatherTupleMembers tupleInfo
        |> Seq.mapi (fun i (pI, path) -> 
            let label = sprintf "Item%d" (i+1)
            mkWriteMemberUntyped<'Tuple> label pI path)
        |> Seq.toArray

    let fieldStack = gatherNestedFields tupleInfo

    member __.IsStructTuple = isStructTuple
    /// Tuple element shape definitions
    member __.Elements = tupleElems
    /// Creates an uninitialized tuple instance of given type
    member __.CreateUninitialized() : 'Tuple =
        if isStructTuple then Unchecked.defaultof<'Tuple>
        else
            let obj = FormatterServices.GetUninitializedObject typeof<'Tuple>
            let mutable this = obj
            for f in fieldStack do
                let x = FormatterServices.GetUninitializedObject f.FieldType
                f.SetValue(this, x)
                this <- x

            obj :?> 'Tuple

#if TYPESHAPE_EXPR
    member __.CreateUninitializedExpr() : Expr<'Tuple> =
        if isStructTuple then
            Expr.Cast<'Tuple>(Expr.DefaultValue typeof<'Tuple>)
        else
            let values = tupleElems |> Seq.map (fun e -> getDefaultValueExpr e.Member.Type) |> Seq.toList
            Expr.Cast<'Tuple>(Expr.NewTuple(values))
#endif

    interface IShapeTuple with
        member __.Elements = tupleElems |> Array.map (fun e -> e :> _)
        member __.Accept v = v.Visit __

//---------------------
// F# Records

/// Denotes an F# record type
type IShapeFSharpRecord =
    /// F# record field shapes
    abstract Fields : IShapeMember[]
    abstract Accept : IFSharpRecordVisitor<'R> -> 'R

/// Identifies an F# record type
and [<Sealed>] ShapeFSharpRecord<'Record> private () =
    let isStructRecord = typeof<'Record>.IsValueType
    let ctorInfo = FSharpValue.PreComputeRecordConstructorInfo(typeof<'Record>, allMembers)
    let props = FSharpType.GetRecordFields(typeof<'Record>, allMembers)
    let fields = typeof<'Record>.GetFields(allInstanceMembers)
    let mkRecordField (prop : PropertyInfo) =
        let field = fields |> Array.find (fun f -> f.Name = prop.Name + "@")
        mkWriteMemberUntyped<'Record> prop.Name prop [|field :> MemberInfo|]

#if TYPESHAPE_EMIT
    let ctorf = emitUninitializedCtor<'Record> ctorInfo
#else
    let ctorParams = props |> Array.map (fun p -> defaultOfUntyped p.PropertyType)
#endif

    let recordFields = Array.map mkRecordField props

    member __.IsStructRecord = isStructRecord

    /// F# record field shapes
    member __.Fields = recordFields

    /// Creates an uninitialized instance for given record
    member __.CreateUninitialized() : 'Record =
        if isStructRecord then Unchecked.defaultof<'Record> else
#if TYPESHAPE_EMIT
        ctorf.Value.Invoke()
#else
        ctorInfo.Invoke ctorParams :?> 'Record
#endif

#if TYPESHAPE_EXPR
    member __.CreateUninitializedExpr() : Expr<'Record> =
        if isStructRecord then <@ Unchecked.defaultof<'Record> @> else
        let values = props |> Seq.map (fun p -> getDefaultValueExpr p.PropertyType)
        Expr.Cast<'Record>(Expr.NewObject(ctorInfo, Seq.toList values))
#endif

    interface IShapeFSharpRecord with
        member __.Fields = recordFields |> Array.map unbox
        member __.Accept v = v.Visit __

and IFSharpRecordVisitor<'R> =
    abstract Visit : ShapeFSharpRecord<'Record> -> 'R

//----------------------
// F# Unions

/// Denotes an F# union case shape
type IShapeFSharpUnionCase =
    /// Underlying FSharp.Reflection.UnionCaseInfo description
    abstract CaseInfo : UnionCaseInfo
    /// Field shapes for union case
    abstract Fields : IShapeMember[]

/// Denotes an F# union case shape
type [<Sealed>] ShapeFSharpUnionCase<'Union> private (uci : UnionCaseInfo) =
    let properties = uci.GetFields()
    let ctorInfo = FSharpValue.PreComputeUnionConstructorInfo(uci, allMembers)
#if TYPESHAPE_EMIT
    let ctorf = emitUninitializedCtor<'Union> ctorInfo
#else
    let ctorParams = properties |> Array.map (fun p -> defaultOfUntyped p.PropertyType)
#endif

    let caseFields =
        match properties with
        | [||] -> [||]
        | _ ->
            let underlyingType = properties.[0].DeclaringType
            let allFields = underlyingType.GetFields(allInstanceMembers)
            let mkUnionField (p : PropertyInfo) =
                let fieldInfo = allFields |> Array.find (fun f -> f.Name = "_" + p.Name || f.Name.ToLower() = p.Name.ToLower())
                mkWriteMemberUntyped<'Union> p.Name p [|fieldInfo|]

            Array.map mkUnionField properties

    /// Underlying FSharp.Reflection.UnionCaseInfo description
    member __.CaseInfo = uci
    /// Field shapes for union case
    member __.Fields = caseFields

    /// Creates an uninitialized instance for specific union case
    member __.CreateUninitialized() : 'Union =

        ctorInfo.Invoke(null, ctorParams) :?> 'Union


    interface IShapeFSharpUnionCase with
        member __.CaseInfo = uci
        member __.Fields = caseFields |> Array.map (fun f -> f :> _)

/// Denotes an F# Union shape
type IShapeFSharpUnion =
    /// Case shapes for given union type
    abstract UnionCases : IShapeFSharpUnionCase[]
    abstract Accept : IFSharpUnionVisitor<'R> -> 'R

/// Denotes an F# Union shape
and [<Sealed>] ShapeFSharpUnion<'U> private () =
    let isStructUnion = typeof<'U>.IsValueType
    let ucis = 
        FSharpType.GetUnionCases(typeof<'U>, allMembers)
        |> Array.map (fun uci -> 
            Activator.CreateInstanceGeneric<ShapeFSharpUnionCase<'U>>([||],[|uci|]) 
            :?> ShapeFSharpUnionCase<'U>)

#if TYPESHAPE_EMIT || TYPESHAPE_EXPR
    let tagReaderInfo = FSharpValue.PreComputeUnionTagMemberInfo(typeof<'U>, allMembers)
#endif

#if TYPESHAPE_EMIT
    let tagReader = emitUnionTagReader<'U> tagReaderInfo
#else
    let tagReader = FSharpValue.PreComputeUnionTagReader(typeof<'U>, allMembers)
#endif

    let caseNames = ucis |> Array.map (fun u -> u.CaseInfo.Name)

    member __.IsStructUnion = isStructUnion

    /// Case shapes for given union type
    member __.UnionCases = ucis
    /// Gets the underlying tag id for given union instance
    member __.GetTag (union : 'U) : int = 
#if TYPESHAPE_EMIT
        tagReader.Value.Invoke union
#else
        tagReader union
#endif

    /// Gets the underlying tag id for given union case name
    member __.GetTag (caseName : string) : int =
        let caseNames = caseNames
        let n = caseNames.Length
        let mutable i = 0
        let mutable notFound = true
        while notFound && i < n do
            if caseNames.[i] = caseName then
                notFound <- false
            else
                i <- i + 1
        if notFound then raise <| KeyNotFoundException(sprintf "Union case: %A" caseName)
        i

#if TYPESHAPE_EXPR
    member __.GetTagExpr (union : Expr<'U>) : Expr<int> =
        let expr =
            match tagReaderInfo with
            | :? MethodInfo as m when m.IsStatic -> Expr.Call(m, [union])
            | :? MethodInfo as m -> Expr.Call(union, m, [])
            | :? PropertyInfo as p -> Expr.PropertyGet(union, p)
            | _ -> invalidOp <| sprintf "Unexpected tag reader info %O" tagReaderInfo
        
        Expr.Cast<int> expr
#endif
        
        
    interface IShapeFSharpUnion with
        member __.UnionCases = ucis |> Array.map (fun u -> u :> _)
        member __.Accept v = v.Visit __

and IFSharpUnionVisitor<'R> =
    abstract Visit : ShapeFSharpUnion<'U> -> 'R

//------------------------
// C# Records

/// Denotes a type that behaves like a mutable C# record:
/// Carries a parameterless constructor and settable properties
type IShapeCliMutable =
    /// Gettable and Settable properties for C# Record
    abstract Properties : IShapeMember[]
    abstract Accept : ICliMutableVisitor<'R> -> 'R

/// Denotes a type that behaves like a C# record:
/// Carries a parameterless constructor and settable properties
and [<Sealed>] ShapeCliMutable<'Record> private (defaultCtor : ConstructorInfo) =
    let properties =
        typeof<'Record>.GetProperties(allInstanceMembers)
        |> Seq.filter (fun p -> p.CanRead && p.CanWrite && p.GetIndexParameters().Length = 0)
        |> Seq.map (fun p -> mkWriteMemberUntyped<'Record> p.Name p [|p|])
        |> Seq.toArray

#if TYPESHAPE_EMIT
    let ctor = emitUninitializedCtor<'Record> defaultCtor
#endif

    /// Creates an uninitialized instance for given C# record
    member __.CreateUninitialized() : 'Record = 
#if TYPESHAPE_EMIT
        ctor.Value.Invoke()
#else
        defaultCtor.Invoke [||] :?> 'Record
#endif

#if TYPESHAPE_EXPR
    /// Creates an uninitialized instance for given C# record
    member __.CreateUninitializedExpr() = 
        Expr.Cast<'Record>(Expr.NewObject(defaultCtor, []))
#endif

    /// Property shapes for C# record
    member __.Properties = properties
    /// Gets the default constructor info defined in the type
    member __.DefaultCtorInfo = defaultCtor

    interface IShapeCliMutable with
        member __.Properties = properties |> Array.map (fun p -> p :> _)
        member __.Accept v = v.Visit __

and ICliMutableVisitor<'R> =
    abstract Visit : ShapeCliMutable<'Record> -> 'R

//--------------------------
// Shape POCO

/// Denotes any .NET type that is either a class or a struct
type IShapePoco =
    /// True iff POCO is a struct
    abstract IsStruct : bool
    /// Constructor shapes for the type
    abstract Constructors : IShapeConstructor[]
    /// Field shapes for the type
    abstract Fields : IShapeMember[]
    /// Property shapes for the type
    abstract Properties : IShapeMember[]
    abstract Accept : IPocoVisitor<'R> -> 'R

/// Denotes any .NET type that is either a class or a struct
and [<Sealed>] ShapePoco<'Poco> private () =
    let isStruct = typeof<'Poco>.IsValueType

    let fields = 
        typeof<'Poco>.GetFields(allInstanceMembers)
        |> Array.map (fun f -> mkWriteMemberUntyped<'Poco> f.Name f [|f|])

    let ctors =
        typeof<'Poco>.GetConstructors(allInstanceMembers)
        // filter any ctors that accept byrefs or pointers
        |> Seq.filter (fun c -> c.GetParameters() |> Array.exists(fun p -> let t = p.ParameterType in t.IsPointer || t.IsByRef) |> not)
        |> Seq.map (fun c -> mkCtorUntyped<'Poco> c)
        |> Seq.toArray

    let properties =
        typeof<'Poco>.GetProperties(allInstanceMembers)
        |> Array.map (fun p -> mkMemberUntyped<'Poco> p.Name p [|p|])

    /// True iff POCO is a struct
    member __.IsStruct = isStruct
    /// Constructor shapes for the type
    member __.Constructors = ctors
    /// Field shapes for the type
    member __.Fields = fields
    /// Property shapes for the type
    member __.Properties = properties

    /// Creates an uninitialized instance for POCO
    member inline __.CreateUninitialized() : 'Poco = 
        FormatterServices.GetUninitializedObject(typeof<'Poco>) :?> 'Poco

#if TYPESHAPE_EXPR
    /// Creates an uninitialized instance for POCO
    member inline __.CreateUninitializedExpr() : Expr<'Poco> =
        <@ FormatterServices.GetUninitializedObject(typeof<'Poco>) :?> 'Poco @>
#endif

    interface IShapePoco with
        member __.Constructors = ctors |> Array.map (fun c -> c :> _)
        member __.Fields = fields |> Array.map (fun f -> f :> _)
        member __.Properties = properties |> Array.map (fun p -> p :> _)
        member __.IsStruct = isStruct
        member __.Accept v = v.Visit __

and IPocoVisitor<'R> =
    abstract Visit : ShapePoco<'Poco> -> 'R


//-----------------------------
// Section: Shape ISerializable

type ISerializableVisitor<'R> =
    abstract Visit<'T when 'T :> ISerializable> : ShapeISerializable<'T> -> 'R

and IShapeISerializable =
    abstract CtorInfo : ConstructorInfo
    abstract Accept : ISerializableVisitor<'R> -> 'R

and ShapeISerializable<'T when 'T :> ISerializable> private () =
    let ctorTypes = [|typeof<SerializationInfo>; typeof<StreamingContext>|]
    let ctorInfo = typeof<'T>.GetConstructor(allInstanceMembers, null, ctorTypes, [||])
    let getCtorInfo () =
        match ctorInfo with
        | null -> invalidOp <| sprintf "ISerializable constructor not available for type '%O'" typeof<'T>
        | ctor -> ctor

#if TYPESHAPE_EMIT
    let ctor = lazy(let c = emitISerializableCtor<'T> (getCtorInfo()) in c.Value)
#endif

    member __.CtorInfo = ctorInfo
    member __.Create(serializationInfo : SerializationInfo, streamingContext : StreamingContext) : 'T =
#if TYPESHAPE_EMIT
        ctor.Value.Invoke(serializationInfo, streamingContext)
#else
        getCtorInfo().Invoke [| serializationInfo ; streamingContext |] :?> 'T
#endif

#if TYPESHAPE_EXPR
    member __.CreateExpr (serializationInfo : Expr<SerializationInfo>) (streamingContext : Expr<StreamingContext>) : Expr<'T> =
        Expr.Cast<'T>(Expr.NewObject(getCtorInfo(), [serializationInfo; streamingContext]))
#endif

    interface IShapeISerializable with
        member __.CtorInfo = ctorInfo
        member __.Accept v = v.Visit<'T> __

//--------------------------------------
// Section: TypeShape active recognizers

[<RequireQualifiedAccess>]
module Shape =

    let private SomeU = Some() // avoid allocating all the time
    let inline private test<'T> (s : TypeShape) =
        match s with
        | :? TypeShape<'T> -> SomeU
        | _ -> None

    // ----------
    // Primitives

    let (|Bool|_|) s = test<bool> s
    let (|Byte|_|) s = test<byte> s
    let (|SByte|_|) s = test<sbyte> s
    let (|Int16|_|) s = test<int16> s
    let (|Int32|_|) s = test<int32> s
    let (|Int64|_|) s = test<int64> s
    let (|IntPtr|_|) s = test<nativeint> s
    let (|UInt16|_|) s = test<uint16> s
    let (|UInt32|_|) s = test<uint32> s
    let (|UInt64|_|) s = test<uint64> s
    let (|UIntPtr|_|) s = test<unativeint> s
    let (|Single|_|) s = test<single> s
    let (|Double|_|) s = test<double> s
    let (|Char|_|) s = test<char> s
    let (|Primitive|_|) (s:TypeShape) =
        if s.Type.IsPrimitive then SomeU
        else None

#if !TYPESHAPE_DISABLE_BIGINT
    let (|BigInt|_|) s = test<bigint> s
#endif

    let (|String|_|) s = test<string> s
    let (|Guid|_|) s = test<Guid> s
    let (|Uri|_|) s = test<Uri> s
    let (|Decimal|_|) s = test<decimal> s
    let (|TimeSpan|_|) s = test<TimeSpan> s
    let (|DateTime|_|) s = test<DateTime> s
    let (|DateTimeOffset|_|) s = test<DateTimeOffset> s
    let (|Unit|_|) s = test<unit> s
    let (|FSharpUnit|_|) s = test<unit> s
    let (|ByteArray|_|) s = test<byte []> s
    
    /// Recognizes any type that is a System.Nullable instance
    let (|Nullable|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Nullable<_>> ->
            Activator.CreateInstanceGeneric<ShapeNullable<_>>(ta) 
            :?> IShapeNullable
            |> Some

        | _ -> None
        
    /// Recognizes any type that is a .NET enumeration
    let (|Enum|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Enum(e,u) ->
            Activator.CreateInstanceGeneric<ShapeEnum<BindingFlags, int>> [|e;u|]
            :?> IShapeEnum 
            |> Some
        | _ -> None

    /// Recognizes any type that satisfies the F# `equality` constraint
    let (|Equality|_|) (s : TypeShape) =
        // Since equality & comparison constraints are not contained
        // in reflection metadata, we need to separately determine 
        // whether they are satisfied
        // c.f. Section 5.2.10 of the F# Spec
        let rec isEqualityConstraint (stack:Type list) (t:Type) =
            if stack |> List.exists ((=) t) then true // recursive paths resolve to true always
            elif FSharpType.IsUnion(t, allMembers) then 
                if t.IsValueType then
                    t.GetProperties(allMembers)
                    |> Seq.filter (fun p -> p.Name <> "Tag")
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isEqualityConstraint (t :: stack))

                elif t.ContainsAttr<NoEqualityAttribute>(true) then false
                elif t.ContainsAttr<CustomEqualityAttribute>(true) then true
                else
                    FSharpType.GetUnionCases(t, allMembers)
                    |> Seq.collect (fun uci -> uci.GetFields())
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isEqualityConstraint (t :: stack))

            elif t.ContainsAttr<NoEqualityAttribute>(false) then false
            elif FSharpType.IsRecord(t, allMembers) then
                if t.ContainsAttr<CustomEqualityAttribute>(true) then false
                else
                    FSharpType.GetRecordFields(t, allMembers)
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isEqualityConstraint (t :: stack))

            elif FSharpType.IsTuple t then
                FSharpType.GetTupleElements t
                |> Seq.distinct
                |> Seq.forall (isEqualityConstraint (t :: stack))

            elif FSharpType.IsFunction t then false
            elif t.IsArray then
                isEqualityConstraint (t :: stack) (t.GetElementType())
            else
                true

        if isEqualityConstraint [] s.Type then
            Activator.CreateInstanceGeneric<ShapeEquality<_>> [|s.Type|]
            :?> IShapeEquality
            |> Some
        else
            None

    /// Recognizes any type that satisfies the F# `comparison` constraint
    let (|Comparison|_|) (s : TypeShape) =
        // Since equality & comparison constraints are not contained
        // in reflection metadata, we need to separately determine 
        // whether they are satisfied
        // c.f. Section 5.2.10 of the F# Spec
        let rec isComparisonConstraint (stack:Type list) (t:Type) =
            if t = typeof<IntPtr> || t = typeof<UIntPtr> then true
            elif stack |> List.exists ((=) t) then true // recursive paths resolve to true always
            elif FSharpType.IsUnion(t, allMembers) then 
                if t.IsValueType then
                    t.GetProperties(allMembers)
                    |> Seq.filter (fun p -> p.Name <> "Tag")
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isComparisonConstraint (t :: stack))

                elif t.ContainsAttr<NoComparisonAttribute>(true) then false 
                elif t.ContainsAttr<CustomComparisonAttribute>(true) then true
                else
                    FSharpType.GetUnionCases(t, allMembers)
                    |> Seq.collect (fun uci -> uci.GetFields())
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isComparisonConstraint (t :: stack))

            elif t.ContainsAttr<NoComparisonAttribute>(false) then false
            elif FSharpType.IsRecord(t, allMembers) then
                if t.ContainsAttr<CustomComparisonAttribute>(true) then false
                else
                    FSharpType.GetRecordFields(t, allMembers)
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isComparisonConstraint (t :: stack))

            elif FSharpType.IsTuple t then
                FSharpType.GetTupleElements t
                |> Seq.distinct
                |> Seq.forall (isComparisonConstraint (t :: stack))

            elif t.IsArray then
                isComparisonConstraint (t :: stack) (t.GetElementType())

            elif isInterfaceAssignableFrom typeof<IComparable> t then true
            else
                false

        if isComparisonConstraint [] s.Type then
            Activator.CreateInstanceGeneric<ShapeComparison<_>> [|s.Type|]
            :?> IShapeComparison
            |> Some
        else
            None

    /// Identifies whether shape satisfies the 'struct' or 'not struct' constraint
    let (|Struct|NotStruct|) (s : TypeShape) =
        if s.Type.IsValueType then
            let instance = Activator.CreateInstanceGeneric<ShapeStruct<_>> [|s.Type|] :?> IShapeStruct
            Struct instance
        else
            let instance = Activator.CreateInstanceGeneric<ShapeNotStruct<_>> [|s.Type|] :?> IShapeNotStruct
            NotStruct instance

    /// Recognizes shapes that carry a parameterless constructor
    let (|DefaultConstructor|_|) (shape : TypeShape) =
        match shape.Type.GetConstructor(BindingFlags.Public ||| BindingFlags.Instance, null, [||], [||]) with
        | null -> None
        | _ -> 
            Activator.CreateInstanceGeneric<ShapeDefaultConstructor<_>>([|shape.Type|]) 
            :?> IShapeDefaultConstructor
            |> Some

    /// Recognizes shapes that are instances of System.Collections.Generic.KeyValuePair<_,_>
    let (|KeyValuePair|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<KeyValuePair<_,_>> ->
            Activator.CreateInstanceGeneric<ShapeKeyValuePair<_,_>>(ta)
            :?> IShapeKeyValuePair
            |> Some
        | _ ->
            None

    /// Recognizes shapes that are instances of System.Collections.Generic.Dictionary<_,_>
    let (|Dictionary|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Dictionary<_,_>> ->
            Activator.CreateInstanceGeneric<ShapeDictionary<_,_>>(ta)
            :?> IShapeDictionary
            |> Some
        | _ ->
            None

    /// Recognizes shapes that are instances of System.Collections.Generic.HashSet<_>
    let (|HashSet|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<HashSet<_>> ->
            Activator.CreateInstanceGeneric<ShapeHashSet<_>>(ta)
            :?> IShapeHashSet
            |> Some
        | _ ->
            None

    /// Recognizes shapes that are instances of System.Collections.Generic.List<_>
    let (|ResizeArray|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<ResizeArray<_>> ->
            Activator.CreateInstanceGeneric<ShapeResizeArray<_>>(ta)
            :?> IShapeResizeArray
            |> Some
        | _ ->
            None

    /// Recognizes shapes that inherit from System.Delegate
    let (|Delegate|_|) (s : TypeShape) =
        if typeof<System.Delegate>.IsAssignableFrom s.Type then
            Activator.CreateInstanceGeneric<ShapeDelegate<_>>([|s.Type|])
            :?> IShapeDelegate
            |> Some
        else
            None

    /// Recognizes shapes that inherit from System.Exception
    let (|Exception|_|) (s : TypeShape) =
        if typeof<System.Exception>.IsAssignableFrom s.Type then
            let isFSharpExn = FSharpType.IsExceptionRepresentation(s.Type, allMembers)
            Activator.CreateInstanceGeneric<ShapeException<_>>([|s.Type|], [|isFSharpExn|])
            :?> IShapeException
            |> Some
        else
            None

    /// Recognizes shapes that implement ISerializable
    let (|ISerializable|_|) (shape : TypeShape) =
        if typeof<ISerializable>.IsInterfaceAssignableFrom shape.Type then
            Activator.CreateInstanceGeneric<ShapeISerializable<_>>([|shape.Type|])
            :?> IShapeISerializable
            |> Some
        else
            None

    /// Recognizes shapes that are .NET arrays
    let (|Array|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | TypeShapeInfo.Array(et,rk) ->
            Activator.CreateInstanceGeneric<ShapeArray<_>>([|et|], [|box rk|])
            :?> IShapeArray
            |> Some
        | _ ->
            None

    let (|SystemArray|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | TypeShapeInfo.Array(et, rk) ->
            Activator.CreateInstanceGeneric<ShapeSystemArray<_>>([|s.Type|], [|box et; box rk|])
            :?> IShapeSystemArray
            |> Some
        | _ ->
            None

    /// Recognizes instances of System.Tuple<_>
    let (|Tuple1|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Tuple<_>> ->
            Activator.CreateInstanceGeneric<ShapeTuple1<_>>(ta)
            :?> IShapeTuple1
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_>
    let (|Tuple2|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple2<_,_>>(ta)
            :?> IShapeTuple2
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_>
    let (|Tuple3|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple3<_,_,_>>(ta)
            :?> IShapeTuple3
            |> Some
        | _ -> None
        
    /// Recognizes instances of System.Tuple<_,_,_,_>
    let (|Tuple4|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple4<_,_,_,_>>(ta)
            :?> IShapeTuple4
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_>
    let (|Tuple5|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple5<_,_,_,_,_>>(ta)
            :?> IShapeTuple5
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_,_>
    let (|Tuple6|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple6<_,_,_,_,_,_>>(ta)
            :?> IShapeTuple6
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_,_,_>
    let (|Tuple7|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple7<_,_,_,_,_,_,_>>(ta)
            :?> IShapeTuple7
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_,_,_,_>
    let (|Tuple8|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Tuple<_,_,_,_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeTuple8<_,_,_,_,_,_,_,_>>(ta)
            :?> IShapeTuple8
            |> Some
        | _ -> None

    /// Recognizes shapes of F# list types
    let (|FSharpList|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ list> ->
            Activator.CreateInstanceGeneric<ShapeFSharpList<_>>(ta)
            :?> IShapeFSharpList
            |> Some
        | _ -> None

    /// Recognizes shapes of F# option types
    let (|FSharpOption|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ option> ->
            Activator.CreateInstanceGeneric<ShapeFSharpOption<_>>(ta)
            :?> IShapeFSharpOption
            |> Some
        | _ -> None

    /// Recognizes shapes of F# ref types
    let (|FSharpRef|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ ref> ->
            Activator.CreateInstanceGeneric<ShapeFSharpRef<_>>(ta)
            :?> IShapeFSharpRef
            |> Some
        | _ -> None

    /// Recognizes shapes of F# set types
    let (|FSharpSet|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Set<_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpSet<_>>(ta)
            :?> IShapeFSharpSet
            |> Some
        | _ -> None

    /// Recognizes shapes of F# map types
    let (|FSharpMap|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Map<_,_>> -> 
            Activator.CreateInstanceGeneric<ShapeFSharpMap<_,_>>(ta)
            :?> IShapeFSharpMap
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_> types
    let (|FSharpChoice2|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_>>(ta)
            :?> IShapeFSharpChoice2
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_> types
    let (|FSharpChoice3|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_>>(ta)
            :?> IShapeFSharpChoice3
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_> types
    let (|FSharpChoice4|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_>>(ta)
            :?> IShapeFSharpChoice4
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_,_> types
    let (|FSharpChoice5|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_,_>>(ta)
            :?> IShapeFSharpChoice5
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_,_,_> types
    let (|FSharpChoice6|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_,_,_>>(ta)
            :?> IShapeFSharpChoice6
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_,_,_,_> types
    let (|FSharpChoice7|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_,_,_,_>>(ta)
            :?> IShapeFSharpChoice7
            |> Some
        | _ -> None

    /// Recognizes shapes of F# function types
    let (|FSharpFunc|_|) (s : TypeShape) =
        if FSharpType.IsFunction s.Type then
            let d,c = FSharpType.GetFunctionElements s.Type
            Activator.CreateInstanceGeneric<ShapeFSharpFunc<_,_>> [|d;c|]
            :?> IShapeFSharpFunc
            |> Some
        else None

    /// Recognizes shapes that implement System.Collections.Generic.ICollection<_>
    let (|Collection|_|) (s : TypeShape) =
        match s.Type.GetInterface("ICollection`1") with
        | null ->
            match s.ShapeInfo with
            | Generic(td,ta) when td = typedefof<ICollection<_>> ->
                Activator.CreateInstanceGeneric<ShapeCollection<_,_>> [|s.Type; ta.[0]|]
                :?> IShapeCollection
                |> Some
            | _ -> None
        | iface ->
            let args = iface.GetGenericArguments()
            Activator.CreateInstanceGeneric<ShapeCollection<_,_>> [|s.Type; args.[0]|]
            :?> IShapeCollection
            |> Some

    /// Recognizes shapes that implement System.Collections.Generic.IEnumerable<_>
    let (|Enumerable|_|) (s : TypeShape) =
        match s.Type.GetInterface("IEnumerable`1") with
        | null ->
            match s.ShapeInfo with
            | Generic(td,ta) when td = typedefof<IEnumerable<_>> ->
                Activator.CreateInstanceGeneric<ShapeEnumerable<_,_>> [|s.Type; ta.[0]|]
                :?> IShapeEnumerable
                |> Some
            | _ -> None
        | iface ->
            let args = iface.GetGenericArguments()
            Activator.CreateInstanceGeneric<ShapeEnumerable<_,_>> [|s.Type; args.[0]|]
            :?> IShapeEnumerable
            |> Some

    /// Recognizes shapes that are F# records
    let (|FSharpRecord|_|) (s : TypeShape) =
        if FSharpType.IsRecord(s.Type, allMembers) then
            Activator.CreateInstanceGeneric<ShapeFSharpRecord<_>>([|s.Type|], [||])
            :?> IShapeFSharpRecord
            |> Some
        else
            None

    /// Recognizes shapes that are F# unions
    let (|FSharpUnion|_|) (s : TypeShape) =
        if FSharpType.IsUnion(s.Type, allMembers) then
            if s.Type.IsValueType && fsharpCoreRuntimeVersion < fsharpCore41Version then
                sprintf "TypeShape error: FSharp.Core Runtime %A does not support struct unions. %A or later is required"
                    fsharpCoreRuntimeVersion fsharpCore41Version
                |> invalidOp

            Activator.CreateInstanceGeneric<ShapeFSharpUnion<_>>([|s.Type|], [||])
            :?> IShapeFSharpUnion
            |> Some
        else
            None

    /// Recognizes shapes that are System.Tuple instances of arbitrary arity
    let (|Tuple|_|) (s : TypeShape) =
        if FSharpType.IsTuple s.Type then
            Activator.CreateInstanceGeneric<ShapeTuple<_>>([|s.Type|], [||])
            :?> IShapeTuple
            |> Some
        else
            None

    /// Recognizes shapes that look like C# record classes
    /// They are classes with parameterless constructors and settable properties
    let (|CliMutable|_|) (s : TypeShape) =
        match s.Type.GetConstructor(allInstanceMembers, null, [||], [||]) with
        | null -> None
        | ctor -> 
            Activator.CreateInstanceGeneric<ShapeCliMutable<_>>([|s.Type|], [|ctor|])
            :?> IShapeCliMutable
            |> Some

    /// Recognizes POCO shapes, .NET types that are either classes or structs
    let (|Poco|_|) (s : TypeShape) =
        if s.Type.IsClass || s.Type.IsValueType then
            let hasPointers = 
                s.Type.GetFields allInstanceMembers 
                |> Seq.map (fun f -> f.FieldType)
                |> Seq.exists (fun t -> t.IsByRef || t.IsPointer)

            if hasPointers then None // do not recognize if type has pointer fields
            else
                Activator.CreateInstanceGeneric<ShapePoco<_>>([|s.Type|], [||])
                :?> IShapePoco
                |> Some
        else
            None


//open System
//open TypeShape
//open TypeShape_Utils














open System
open System.Threading
open System.Collections.Generic
open System.Collections.Concurrent

/// Value container that will eventually be populated
type Cell<'T> internal (container : 'T option ref) =
    let mutable isCreated = false
    let mutable value = Unchecked.defaultof<'T>
    member __.IsValueCreated : bool = 
        if isCreated then true else
        match !container with
        | None -> false
        | Some t -> value <- t ; isCreated <- true ; true

    member __.Value : 'T = 
        if isCreated then value else
        match !container with
        | None -> failwithf "Value for '%O' has not been initialized." typeof<'T>
        | Some t -> value <- t ; isCreated <- true ; t

[<NoEquality; NoComparison>]
type private RecTypePayload = { Cell : obj ; Value : obj ; IsValueSet : unit -> bool }

/// Helper class for generating recursive values
type RecTypeManager internal (parentCache : TypeCache option) = 
    let dict = new ConcurrentDictionary<Type, RecTypePayload>()

    new () = new RecTypeManager(None)
    member internal __.ParentCache = parentCache

    /// Attempt to look up value by type.
    /// If uninitialized rectype returns the placeholder dummy value.
    member __.TryGetValue<'T>(result : byref<'T>) : bool =
        let ok, payload = dict.TryGetValue typeof<'T>
        if ok then result <- payload.Value :?> 'T ; true
        else
            match parentCache with
            | None -> false
            | Some pc -> pc.TryGetValue<'T>(&result)

    /// Attempt to look up value by type.
    /// If uninitialized rectype returns the placeholder dummy value.
    member __.TryGetValue(t : Type, result : byref<obj>) : bool =
        let ok, payload = dict.TryGetValue t
        if ok then result <- payload.Value ; true
        else
            match parentCache with
            | None -> false
            | Some pc -> pc.TryGetValue(t, &result)

    /// Attempt to look up value by type.
    /// If uninitialized rectype returns the placeholder dummy value.
    member __.TryFind<'T>() =
        let ok, payload = dict.TryGetValue typeof<'T>
        if ok then Some(payload.Value :?> 'T)
        else
            match parentCache with
            | None -> None
            | Some pc -> pc.TryFind<'T>()

    /// Attempt to look up value by type.
    /// If uninitialized rectype returns the placeholder dummy value.
    member __.TryFind (t : Type) =
        let ok, payload = dict.TryGetValue t
        if ok then Some payload.Value
        else
            match parentCache with
            | None -> None
            | Some pc -> pc.TryFind t

    /// <summary>
    ///     Registers an uninitialized value at the beggining of a recursive
    ///     value definition. Returns a dummy value that can be referenced within a
    ///     recursive flow. Only delayable values can be recursive.
    /// </summary>
    /// <param name="delay">Provides delay wrapping for supplied type.</param>
    member __.CreateUninitialized<'T>(delay : Cell<'T> -> 'T) : 'T =
        let create _ =
            let container = ref None
            let dummy = delay (Cell container)
            { Cell = container ; Value = dummy ; IsValueSet = fun () -> Option.isSome !container }

        let payload = dict.GetOrAdd(typeof<'T>, create)
        payload.Value :?> 'T

    /// Registers a value to the type index. Any uninitialized references 
    /// to this type will be updated to point to this value.
    member __.Complete<'T>(value : 'T) : 'T =
        let create _ =
            { Cell = ref (Some value) ; Value = value ; IsValueSet = fun () -> true }

        let update _ (payload : RecTypePayload) =
            if payload.IsValueSet() then payload 
            else
                lock payload.Cell (fun () ->
                    if payload.IsValueSet() then payload
                    else
                        payload.Cell :?> 'T option ref := Some value
                        { payload with Value = value })

        let payload = dict.AddOrUpdate(typeof<'T>, create, update)
        payload.Value :?> 'T

    member internal __.GetGeneratedValues() =
        let hasIncompleteValues = ref false
        let values =
            dict
            |> Seq.map (function 
                KeyValue(t, payload) ->
                    if not <| payload.IsValueSet() 
                    then hasIncompleteValues := true
                    (t, payload.Value))
            |> Seq.toArray

        if !hasIncompleteValues then [||] else values

    interface IDisposable with
        member __.Dispose() =
            match parentCache with
            | Some pc -> pc.Commit __
            | None -> ()

/// Thread-safe cache of values indexed by type.
and TypeCache internal (dict : ConcurrentDictionary<Type, obj>) =

    new () = TypeCache(new ConcurrentDictionary<_,_>())

    /// Total number of items in cache
    member __.Count = dict.Count
    /// Checks whether the supplied type is contained in cache
    member __.ContainsKey<'T>() = dict.ContainsKey typeof<'T>
    /// Checks whether the supplied type is contained in cache
    member __.ContainsKey(t : Type) = dict.ContainsKey t
    /// Gets all types registered in the cache
    member __.Keys = dict.Keys
    /// Gets all values registered in the cache
    member __.Values = dict.Values

    /// Try looking up cached value by type
    member __.TryGetValue<'T>(result : byref<'T>) : bool =
        let mutable obj = null
        if dict.TryGetValue(typeof<'T>, &obj) then
            result <- obj :?> 'T ; true
        else
            false

    /// Try looking up cached value by type
    member __.TryGetValue(t : Type, result : byref<obj>) : bool =
        let mutable obj = null
        if dict.TryGetValue(t, &obj) then
            result <- obj ; true
        else
            false

    /// Try looking up cached value by type
    member __.TryFind<'T>() : 'T option =
        let mutable obj = null
        if dict.TryGetValue(typeof<'T>, &obj) then Some(obj :?> 'T)
        else None

    /// Try looking up cached value by type
    member __.TryFind(t : Type) : obj option =
        let mutable obj = null
        if dict.TryGetValue(t, &obj) then Some obj
        else None

    /// Try adding value for given type
    member __.TryAdd<'T>(value : 'T) = dict.TryAdd(typeof<'T>, value)

    /// Forces update for value of given type
    member __.ForceAdd<'T>(value : 'T) = dict.[typeof<'T>] <- value

    /// Gets or adds value for given type using supplied factory.
    /// Uses optimistic concurrency
    member __.GetOrAdd<'T>(factory : unit -> 'T) : 'T =
        dict.GetOrAdd(typeof<'T>, fun _ -> factory() :> obj) :?> 'T

    /// Creates a RecTypeManager that is bound to the current cache.
    /// Values generated by the manager can be committed back to the
    /// cache once completed.
    member __.CreateRecTypeManager() = new RecTypeManager(Some __)

    /// Commits the generates state by a completed RecTypeManager instance.
    member __.Commit(manager : RecTypeManager) =
        match manager.ParentCache with
        | Some pc when pc = __ ->
            for k,v in manager.GetGeneratedValues() do 
                ignore(dict.TryAdd(k, v))

        | _ -> invalidArg "manager" "RecTypeManager does not belong to TypeCache context."

    /// Creates a clone of the current cache items
    member __.Clone() =
        let dict2 = new ConcurrentDictionary<Type, obj>(dict)
        new TypeCache(dict2)

/// Provides a binary search implementation for generic values
type BinSearch<'T when 'T : comparison>(inputs : 'T[]) =
    do 
        let duplicates =
            inputs 
            |> Seq.groupBy id
            |> Seq.filter(fun (_,gp) -> Seq.length gp > 1)
            |> Seq.map fst
            |> Seq.toArray

        if duplicates.Length > 0 then
            duplicates 
            |> Seq.map (sprintf "%A") 
            |> String.concat ","
            |> sprintf "duplicate values %s found"
            |> invalidArg "inputs"

    let indices, sortedInputs =
        inputs
        |> Seq.mapi (fun i v -> i,v)
        |> Seq.sortBy snd
        |> Seq.toArray
        |> Array.unzip

    /// Gets the original input array used to form
    /// this binary search implementation
    member __.Values = inputs

    /// Returns an integer indicating the position of the
    /// given value in the source array, or -1 if not found.
    member __.TryFindIndex(value : 'T) : int =
        match sortedInputs.Length with
        | 0 -> -1
        | 1 -> if sortedInputs.[0] = value then 0 else -1
        | n ->
            let mutable found = false
            let mutable lb, ub = 0, n - 1
            let mutable i = 0

            while not found && ub - lb >= 0 do
                i <- (lb + ub) / 2
                match compare value sortedInputs.[i] with
                | 0 -> found <- true
                | c when c < 0 -> ub <- i - 1
                | _ -> lb <- i + 1

            if found then indices.[i] else -1







////////////////////////////////////////////////
// END TYPESHAPE CODE
////////////////////////////////////////////////





// Linqpad style (hardcoded)

let header =
    """
<!DOCTYPE HTML>
<html>
  <head>
    <meta http-equiv="Content-Type" content="text/html;charset=utf-8" />
	<meta http-equiv="X-UA-Compatible" content="IE=edge" />
    <meta name="Generator" content="LINQ to XML, baby!" />
    <style type='text/css'>
body {
	margin: 0.3em 0.3em 0.4em 0.4em;
	font-family: Verdana;
	font-size: 80%;
	background: white
}

p, pre {
	margin:0;
	padding:0;
	font-family: Verdana;
}

table {
	border-collapse: collapse;
	border: 2px solid #17b;
	margin: 0.3em 0.2em;
}

table.limit {
	border-bottom-color: #c31;
}

table.expandable {
	border-bottom-style: dashed;
}

table.error {
	border-bottom-width: 4px;
}

td, th {
	vertical-align: top;
	border: 1px solid #aaa;
	padding: 0.1em 0.2em;
	margin: 0;
}

th {
	text-align: left;
	background-color: #ddd;
	border: 1px solid #777;
	font-family: tahoma;
	font-size:90%;
	font-weight: bold;
}

th.member {
	padding: 0.1em 0.2em 0.1em 0.2em;
}

td.typeheader {
	font-family: tahoma;
	font-size: 100%;
	font-weight: bold;
	background-color: #17b;
	color: white;
	padding: 0 0.2em 0.15em 0.1em;
}

td.n { text-align: right }

a:link.typeheader, a:visited.typeheader, a:link.extenser, a:visited.extenser, a:link.fixedextenser, a:visited.fixedextenser {
	font-family: tahoma;
	font-size: 90%;
	font-weight: bold;
	text-decoration: none;
	background-color: #17b;
	color: white;
	float:left;
}

a:link.extenser, a:visited.extenser, a:link.fixedextenser, a:visited.fixedextenser {
	float:right; 
	padding-left:2pt;
	margin-left:4pt
}

span.typeglyph, span.typeglyphx {
	padding: 0 0.2em 0 0;
	margin: 0;
}

span.extenser, span.extenserx, span.fixedextenser {	
	margin-top:1.2pt;
}

span.typeglyph, span.extenser, span.fixedextenser {
	font-family: webdings;
}

span.fixedextenser {
	display:none;
	position:fixed;
	right:6px;
}

td.typeheader:hover .fixedextenser {
	display:block
}

span.typeglyphx, span.extenserx {
	font-family: arial;
	font-weight: bold;
	margin: 2px;
}

table.group {
	border: none;
	margin: 0;
}

td.group {
	border: none;
	padding: 0 0.1em;
}

div.spacer { margin: 0.6em 0; }

table.headingpresenter {
	border: none;
	border-left: 3px dotted #1a5;
	margin: 1em 0em 1.2em 0.15em;
}

th.headingpresenter {
	font-family: Arial;
	border: none;
	padding: 0 0 0.2em 0.5em;
	background-color: white;
	color: green;
	font-size: 110%;        
}

td.headingpresenter {
	border: none;
	padding: 0 0 0 0.6em;
}

td.summary { 
	background-color: #def;
	color: #024;
	font-family: Tahoma;
	padding: 0 0.1em 0.1em 0.1em;
}

td.columntotal {
	font-family: Tahoma;
	background-color: #eee;
	font-weight: bold;
	color: #17b;
	font-size:90%;
	text-align:right;
}

span.graphbar {
	background: #17b;
	color: #17b;
	margin-left: -2px;
	margin-right: -2px;
}

a:link.graphcolumn, a:visited.graphcolumn {
	color: #17b;
	text-decoration: none;
	font-weight: bold;
	font-family: Arial;
	font-size: 110%;
	letter-spacing: -0.2em;	
	margin-left: 0.3em;
	margin-right: 0.1em;
}

a:link.collection, a:visited.collection { color:green }

a:link.reference, a:visited.reference { color:blue }

i { color: green }

em { color:red; }

span.highlight { background: #ff8 }

code { font-family: Consolas }

code.xml b { color:blue; font-weight:normal }
code.xml i { color:maroon; font-weight:normal; font-style:normal }
code.xml em { color:red; font-weight:normal; font-style:normal }
    </style>

    <script language='JavaScript' type='text/javascript'>


	  function toggle(id)
      {
        table = document.getElementById(id);
        if (table == null) return false;
        updown = document.getElementById(id + 'ud');
        if (updown == null) return false;
        if (updown.innerHTML == '5' || updown.innerHTML == '6') {
          expand = updown.innerHTML == '6';
          updown.innerHTML = expand ? '5' : '6';
        } else {
          expand = updown.innerHTML == '˅';
          updown.innerHTML = expand ? '˄' : '˅';
        }
        table.style.borderBottomStyle = expand ? 'solid' : 'dashed';
        elements = table.rows;
        if (elements.length == 0 || elements.length == 1) return false;
        for (i = 1; i != elements.length; i++)
          if (elements[i].id.substring(0,3) != 'sum')
            elements[i].style.display = expand ? 'table-row' : 'none';
        return false;
      }
    
    </script>
  </head>
<body><div class="spacer">
"""

let footer = """</div></body>
</html>"""


////////////////////////////////////////////////////
// END Linqpad style
////////////////////////////////////////////////////




type PrettyPrint =
    | List  of list<PrettyPrint>
    | Table of list<Field>
    | Value of string * string
and Field = {name : string; value : PrettyPrint}



// Generic value to PrettyPrint

let rec mkPrinter<'T> () : 'T -> PrettyPrint =
    let ctx = new RecTypeManager()
    mkPrinterCached<'T> ctx

and mkPrinterCached<'T> (ctx : RecTypeManager) : 'T -> PrettyPrint =
    match ctx.TryFind<'T -> PrettyPrint> () with
    | Some p -> p
    | None ->
        let _ = ctx.CreateUninitialized<'T -> PrettyPrint>(fun c t -> c.Value t)
        let p = mkPrinterAux<'T> ctx
        ctx.Complete p

and mkPrinterAux<'T> (ctx : RecTypeManager) : 'T -> PrettyPrint =
    let wrap(p : 'a -> PrettyPrint) = unbox<'T -> PrettyPrint> p
    let mkFieldPrinter (field : IShapeMember<'DeclaringType>) =
        field.Accept {
            new IMemberVisitor<'DeclaringType, string * ('DeclaringType -> PrettyPrint)> with
                member __.Visit(field : ShapeMember<'DeclaringType, 'Field>) =
                    let fp = mkPrinterCached<'Field> ctx
                    field.Label, fp << field.Project
        }

    match shapeof<'T> with
    | Shape.Unit -> wrap (fun v -> Value ("Unit", "()"))
    | Shape.Bool -> wrap (fun v -> Value ("Boolean", sprintf "%b" v))
    | Shape.Byte -> wrap (fun (v:byte) -> Value ("Byte", sprintf "%duy" v))
    | Shape.Int32  -> wrap (fun v -> Value ("Int"  , string<int> v))
    | Shape.Int64  -> wrap (fun v -> Value ("Int64", string<int64> v))
    | Shape.Double  -> wrap (fun v -> Value ("Float", string<float> v))
    | Shape.String -> wrap (fun v -> Value ("String", v))
    | Shape.DateTime       -> wrap (fun (b:DateTime) -> Value ("DateTime", sprintf "(%i, %i, %i, %i, %i, %i, %i)" b.Year b.Month b.Day b.Hour b.Minute b.Second b.Millisecond))

    // | Shape.FSharpOption s -> TODO

    | Shape.FSharpList s ->
        s.Accept { new IFSharpListVisitor<'T -> PrettyPrint> with member __.Visit<'a> () = let tp = mkPrinterCached<'a> ctx in wrap (List.map tp >> List) }

    | Shape.Array s when s.Rank = 1 ->
        s.Accept { new IArrayVisitor<'T ->      PrettyPrint> with member __.Visit<'a> _  = let tp = mkPrinterCached<'a> ctx in wrap (Array.map tp >> Array.toList >> List) }

    | Shape.FSharpSet s ->
        s.Accept { new IFSharpSetVisitor<'T -> PrettyPrint> with member __.Visit<'a when 'a : comparison> () = let tp = mkPrinterCached<'a> ctx in wrap (fun (s : Set<'a>) -> s |> Seq.map tp |> Seq.toList |> List) }

    | Shape.Tuple (:? ShapeTuple<'T> as shape) ->
        let elemPrinters = shape.Elements |> Array.map mkFieldPrinter
        fun (t:'T) -> elemPrinters |> Seq.map (fun (n, ep) -> {name = n.Replace("Item", "#"); value = ep t}) |> Seq.toList |> Table

    | Shape.FSharpRecord (:? ShapeFSharpRecord<'T> as shape) ->
        let fieldPrinters = shape.Fields |> Array.map mkFieldPrinter
        fun (r:'T) -> fieldPrinters |> Seq.map (fun (name, ep) -> {name = name; value = ep r} ) |> Seq.toList |> Table


    //| Shape.FSharpUnion (:? ShapeFSharpUnion<'T> as shape) -> TODO

    | Shape.Poco (:? ShapePoco<'T> as shape) ->
        let propPrinters = shape.Properties |> Array.map mkFieldPrinter
        fun (r:'T) ->
            propPrinters
            |> Seq.map (fun (name, ep) ->
                let value = ep r
                {name = name; value = value}  ) |> Seq.toList |> Table

    | _ -> failwithf "unsupported type '%O'" typeof<'T>


//---------------------------------------

let pprint (x:'t) = let p = mkPrinter<'t>() in p x

//---------------------------------------

// HTML Generation

let rec traversePP x =
    match x with
    | List lst -> 
        let fields =
            lst |> Seq.fold (fun (s:Set<string>) x -> 
                    match x with
                    | Table x ->
                        let c = List.map (fun {name = n} -> n) x
                        set c + s
                    | _ -> s
            ) Set.empty

        let body =
                [
                    yield "<table>"
                    yield "<tr>"
                    for j in fields do
                        yield "<th>" + j + "</th>"
                    yield "</tr>"

                    for e in lst do
                        match e with
                        | Table item ->
                            yield "<tr>"
                            for f in item do
                                yield "<td>" + traversePP f.value + "</td>"
                            yield "</tr>"
                        | other -> yield "<tr><td>" + traversePP other + "</td></tr>"
                    yield "</table>" 
                    ] |> String.concat "  "
        body
     
     | Table fields ->
                        [
                            yield "<table>"                            
                            for f in fields do
                                yield "<tr>"
                                yield "<th>" + f.name + "</th>"
                                yield "<td>" + traversePP f.value + "</td>"
                                yield "</tr>"
                            yield "</table>" 
                            ] |> String.concat "  "
     | Value (ty, vl) -> vl

let genhtml x = header + traversePP x + footer;;



// Floating Window

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
    static member Dump objValue =
        let objName = "RESULTS !" 
        if resultsWdw.IsDisposed then resultsWdw <- getResultsWdw ()
        let localUrl = localUrl ()
        File.WriteAllText (localUrl, objValue |> pprint |> genhtml)
        resultsWdw.FindForm().Text <- title + " - " + objName
        resultsWdw.Url <- Uri localUrl
        objValue







///////////////////////////////////////
// Examples
///////////////////////////////////////



// Single value
Results.Dump 13

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