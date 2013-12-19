﻿// --------------------------------------------------------------------------------------
// Simple monadic parser generator that we use in the IntelliSense
// --------------------------------------------------------------------------------------

#nowarn "40" // recursive references checked at runtime
namespace FSharp.CompilerBinding

open System
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler
open System.Globalization

// --------------------------------------------------------------------------------------
// Simple implementation of LazyList
// --------------------------------------------------------------------------------------

type LazyList<'T> =
  | Nil
  | Cons of 'T * Lazy<LazyList<'T>>

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module LazyList =
  let ofSeq (s:seq<'T>) =
    let en = s.GetEnumerator()
    let rec take() =
      if en.MoveNext() then
        Cons(en.Current, lazy take())
      else
        en.Dispose()
        Nil
    take()

module Parser =

  open System

  /// Add some useful methods for creating strings from sequences
  type System.String with
    static member ofSeq s = new String(s |> Seq.toArray)
    static member ofReversedSeq s = new String(s |> Seq.toArray |> Array.rev)

  /// Parser is implemented using lazy list (so that we can use seq<_>)
  type Parser<'T> = P of (LazyList<char> -> ('T * LazyList<char>) list)


  // Basic functions needed by the computation builder

  let result v = P(fun c -> [v, c])
  let zero () = P(fun c -> [])
  let bind (P p) f = P(fun inp ->
    [ for (pr, inp') in p inp do
        let (P pars) = f pr
        yield! pars inp' ])
  let plus (P p) (P q) = P (fun inp ->
    (p inp) @ (q inp) )

  let (<|>) p1 p2 = plus p1 p2

  type ParserBuilder() =
    member x.Bind(v, f) = bind v f
    member x.Zero() = zero()
    member x.Return(v) = result(v)
    member x.ReturnFrom(p) = p
    member x.Combine(a, b) = plus a b
    member x.Delay(f) = f()

  let parser = new ParserBuilder()

  // --------------------------------------------------------------------------------------
  // Basic combinators for composing parsers

  let item = P(function | LazyList.Nil -> [] | LazyList.Cons(c, r) -> [c,r.Value])

  let sequence (P p) (P q) = P (fun inp -> 
    [ for (pr, inp') in p inp do
        for (qr, inp'') in q inp' do 
          yield (pr, qr), inp''])

  let sat p = parser { 
    let! v = item 
    if (p v) then return v }

  let char x = sat ((=) x)
  let digit = sat Char.IsDigit    
  let lower = sat Char.IsLower
  let upper = sat Char.IsUpper
  let letter = sat Char.IsLetter
  let nondigit = sat (Char.IsDigit >> not)
  let whitespace = sat (Char.IsWhiteSpace)

  let alphanum = parser { 
    return! letter
    return! digit }  

  let rec word = parser {
    return []
    return! parser {
      let! x = letter
      let! xs = word
      return x::xs } }

  let string (str:string) = 
    let chars = str.ToCharArray() |> List.ofSeq
    let rec string' = function
      | [] -> result []
      | x::xs -> parser { 
          let! y = char x
          let! ys = string' xs 
          return y::ys }
    string' chars

  let rec many p = parser {
    return! parser { 
      let! it = p
      let! res = many p
      return it::res } 
    return [] }

  let rec some p = parser {
    let! first = p
    let! rest = many p
    return first::rest }

  let rec map f p = parser { 
    let! v = p 
    return f v }

  let optional p = parser {
    return! parser { let! v = p in return Some v }
    return None }             

  let apply (P p) (str:seq<char>) = 
    let res = str |> LazyList.ofSeq |> p
    res |> List.map fst

// --------------------------------------------------------------------------------------
/// Parsing utilities for IntelliSense (e.g. parse identifier on the left-hand side
/// of the current cursor location etc.)
module Parsing =
  open Parser

  let inline isFirstOpChar ch =
      ch = '!' || ch = '%'|| ch = '&' || ch = '*'|| ch = '+'|| ch = '-'|| ch = '.'|| ch = '/'|| ch = '<'|| ch = '='|| ch = '>'|| ch = '@'|| ch = '^'|| ch = '|'|| ch = '~'
  let isOpChar ch = ch = '?' || isFirstOpChar ch
      
  let private symOpLits = [ "?"; "?<-"; "<@"; "<@@"; "@>"; "@@>" ]

  let isSymbolicOp (str:string) =
    List.exists ((=) str) symOpLits ||
      (str.Length > 1 && isFirstOpChar str.[0] && Seq.forall isOpChar str.[1..])

  let parseSymOpFragment = some (sat isOpChar)
  let parseBackSymOpFragment = parser {
    // This is unfortunate, but otherwise cracking at $ in A.$B
    // causes the backward parse to return a symbol fragment.
    let! c  = sat (fun c -> c <> '.' && isOpChar c)
    let! cs = many (sat isOpChar)
    return String.ofSeq (c :: List.rev cs)
    }

  /// Parses F# short-identifier (i.e. not including '.'); also ignores active patterns
  let parseIdent =
    parseSymOpFragment <|> many (sat PrettyNaming.IsIdentifierPartCharacter)
     |> map String.ofSeq

  let fsharpIdentCharacter = sat PrettyNaming.IsIdentifierPartCharacter

  let rawIdChar = sat (fun c -> c <> '\n' && c <> '\t' && c <> '\r' && c <> '`')

  let singleBackTick = parser {
    let! _ = char '`'
    let! x = rawIdChar
    return ['`';x]
  }

  let rawIdCharAsString = parser {
    let! x = rawIdChar
    return [x]
  }

  // Parse a raw identifier backwards
  let rawIdResidue = parser {
    let! x = many (rawIdCharAsString <|> singleBackTick)
    let! _ = string "``"
    return List.concat x
  }

  /// Parse F# short-identifier and reverse the resulting string
  let parseBackIdent =  
    parser { 
        let! x = optional (string "``")
        let! res = many (if x.IsSome then rawIdChar else fsharpIdentCharacter) |> map String.ofReversedSeq 
        let! _ = optional (string "``") 
        return res }

  /// Parse remainder of a long identifier before '.' (e.g. "Name.space.")
  /// (designed to look backwards - reverses the results after parsing)
  let rec parseBackLongIdentRest = parser {
    return! parser {
      let! _ = char '.'
      let! ident = parseBackIdent
      let! rest = parseBackLongIdentRest
      return ident::rest }
    return [] } 

  /// Parse long identifier with raw residue (backwards) (e.g. "Debug.``A.B Hel")
  /// and returns it as a tuple (reverses the results after parsing)
  let parseBackIdentWithRawResidue = parser {
    let! residue = rawIdResidue
    let residue = String.ofReversedSeq residue
    return! parser {
      let! long = parseBackLongIdentRest
      return residue, long |> List.rev }
    return residue, [] }

  /// Parse long identifier with residue (backwards) (e.g. "Debug.Wri")
  /// and returns it as a tuple (reverses the results after parsing)
  let parseBackIdentWithResidue = parser {
    let! residue = many fsharpIdentCharacter 
    let residue = String.ofReversedSeq residue
    return! parser {
      let! long = parseBackLongIdentRest
      return residue, long |> List.rev }
    return residue, [] }   

  let parseBackIdentWithEitherResidue =
    parseBackIdentWithResidue <|> parseBackIdentWithRawResidue

  /// Parse long identifier and return it as a list (backwards, reversed)
  let parseBackLongIdent = parser {
    return! parser {
      let! ident = parseBackSymOpFragment <|> parseBackIdent
      let! rest = parseBackLongIdentRest
      return ident::rest |> List.rev }
    return [] }

  let parseBackTriggerThenLongIdent = parser {
    let! _ = (char '(' <|> char '<')
    let! _  = many whitespace
    return! parseBackLongIdent
    }

  /// Create sequence that reads the string backwards
  let createBackStringReader (str:string) from = seq { 
    for i in (min from (str.Length - 1)) .. -1 .. 0 do yield str.[i] }

  /// Create sequence that reads the string forwards
  let createForwardStringReader (str:string) from = seq { 
    for i in (max 0 from) .. (str.Length - 1) do yield str.[i] }

  /// Returns first result returned by the parser
  let getFirst p s = apply p s |> List.head
  let tryGetFirst p s = match apply p s with h::_ -> Some h | [] -> None
   