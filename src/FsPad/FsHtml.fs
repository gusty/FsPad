﻿module FsHtml

let private encode(s) = System.Net.WebUtility.HtmlEncode(s)
type Html =
   | Elem of string * Html list
   | Attr of string * string
   | Text of string
   | RawHtml of string
   with
   static member toString elem =
      let rec toString indent elem =
         let spaces = String.replicate indent " "
         match elem with
         | RawHtml(html) -> html
         | Attr(name,value) -> encode(name)+"=\""+encode(value)+"\""
         | Elem(tag, [Text s]) ->
            let tag = encode(tag)
            spaces+"<"+tag+">"+encode(s)+"</"+tag+">\r\n"
         | Elem(tag, content) ->
            let isAttr = function Attr _ -> true | _ -> false
            let attrs, elems = content |> List.partition isAttr
            let attrs =
               if attrs = [] then ""
               else " " + String.concat " " [for attr in attrs -> toString 0 attr]
            let tag = encode(tag)
            match elems with
            | [] -> spaces+"<"+tag+attrs+"/>\r\n"
            | _ ->
               spaces+"<"+tag+attrs+">\r\n"+
                  String.concat "" [for e in elems -> toString (indent+1) e] +
                     spaces+"</"+tag+">\r\n"
         | Text(text) ->
            spaces + encode(text) + "\r\n"
      toString 0 elem
   override this.ToString() = Html.toString this

let elem tag content = Elem(tag,content)
let html = elem "html"
let head = elem "head"
let title = elem "title"
let style = elem "style"
let body = elem "body"
let div = elem "div"
let br = elem "br"
let section = elem "section"
let span = elem "span"
let table = elem "table"
let thead = elem "thead"
let tbody = elem "tbody"
let tfoot = elem "tfoot"
let img = elem "img"
let map = elem "map"
let area = elem "area"
let p = elem "p"
let a = elem "a"
let tr = elem "tr"
let td = elem "td"
let th = elem "th"
let ul = elem "ul"
let li = elem "li"
let h1 = elem "h1"
let h2 = elem "h2"
let h3 = elem "h3"
let h4 = elem "h4"
let strong = elem "strong"
let (~%) s = [Text(s.ToString())]
let (%=) name value = Attr(name,value)