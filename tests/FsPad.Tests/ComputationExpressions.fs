namespace FsPad.Tests

open System
open FsPad
open NUnit.Framework

module Helpers =
    let areEqual (x:'t) (y:'t) = Assert.AreEqual (x, y)

open Helpers

module Printer = 

    [<Test>]
    let basic() =

        // Since it's an FX workflow, the last line should have been executed
        areEqual 1 1

