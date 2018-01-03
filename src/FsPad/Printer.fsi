module FsPad

[<Class>]
type Printer =
    static member Print: 'a -> string
    static member Print: 'a * int -> string