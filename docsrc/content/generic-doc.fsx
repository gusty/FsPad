(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../bin"

(**
Generic operators and functions
===============================

*)

#r @"../../src/FsPad/bin/Release/net45/FsPad.dll"
open FsPad

(**

Generic operators, functions and constants are included in this library.

They work with many types, including:

 1) Existing .NET and F# types

 2) Other types included in this library

 3) Further user defined types

 4) Types defined in other libraries


Case 1 works by using overload resolution inside an internal class (referred to as the Invokable) used at the definition of the generic operation, while all the other cases work typically through Duck Typing, where an expected method name and signature must exists in the target Type or by using default implementations based on other operations.

Here are some examples of the generic ``map`` operation over existing .NET and F# types:

*)
