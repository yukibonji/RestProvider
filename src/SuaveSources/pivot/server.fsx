﻿#I "../../../packages/samples"
#r "Newtonsoft.Json/lib/net40/Newtonsoft.Json.dll"
#r "FSharp.Data/lib/net40/FSharp.Data.dll"
#r "Suave/lib/net40/Suave.dll"
open System
open System.IO
open System.Collections.Generic
open Newtonsoft.Json
open FSharp.Data

open Suave
open Suave.Filters
open Suave.Operators
open Suave.Cookie

// ----------------------------------------------------------------------------
// Provided types for parsing REST provider JSON
// ----------------------------------------------------------------------------

type MemberQuery = JsonProvider<"""
  [ { "name":"United Kingdom", "returns":{"kind":"nested", "endpoint":"/country"}, "trace":["country", "UK"],
      "parameters":[ {"name":"count", "type":null}, {"name":"another", "type":null} ],
      "documentation":"can be a plain string" }, 
    { "name":"United Kingdom", "returns":{"kind":"nested", "endpoint":"/country"},
      "documentation":{"endpoint":"/or-a-url-to-call"} }, 
    { "name":"Population", "returns":{"kind":"primitive", "type":null, "endpoint":"/data"}, "trace":["indicator", "POP"] } ] """>

type TypeInfo = JsonProvider<"""
  [ "float",
    { "name":"record", "fields":[ {"name":"A","type":null} ] },
    { "name":"seq", "params":[ null ] } ]
  """, SampleIsList=true>

// ----------------------------------------------------------------------------
// Operations that we can do on the table
// ----------------------------------------------------------------------------

type Aggregation = 
 | GroupKey
 | CountAll
 | CountDistinct of string
 | ReturnUnique of string
 | ConcatValues of string
 | Sum of string

type SortDirection =
  | Ascending
  | Descending 

type Paging =
  | Take 
  | Skip 
  
type Transformation = 
  | DropColumns of string list
  | SortBy of (string * SortDirection) list
  | GroupBy of string * Aggregation list
  | Paging of string * Paging list
  | Empty

module Transform = 

  let private chunkBy f s = seq {
    let mutable acc = []
    for v in s do 
      if f v then
        yield List.rev acc
        acc <- []
      else acc <- v::acc
    yield List.rev acc }

  let rec private parseAggs acc = function 
    | "key"::rest -> parseAggs (GroupKey::acc) rest
    | "count-all"::rest -> parseAggs (CountAll::acc) rest
    | "count-dist"::fld::rest -> parseAggs (CountDistinct(fld)::acc) rest
    | "unique"::fld::rest -> parseAggs (ReturnUnique(fld)::acc) rest
    | "concat-vals"::fld::rest -> parseAggs (ConcatValues(fld)::acc) rest
    | "sum"::fld::rest -> parseAggs (Sum(fld)::acc) rest
    | [] -> List.rev acc
    | aggs -> failwith (sprintf "Invalid aggregation operation: %s" (String.concat "/" aggs))

  let private parseChunk = function
    | "drop"::columns -> DropColumns(columns)
    | "sort"::columns -> SortBy(columns |> List.chunkBySize 2 |> List.map (function [f; "asc"] -> f, Ascending | [f; "desc"] -> f, Descending | _ -> failwith "Invalid sort by order"))
    | "group"::field::aggs -> GroupBy(field, parseAggs [] aggs)
    | "page"::pgid::ops -> Paging(pgid, List.map (function "take" -> Take | "skip" -> Skip | _ -> failwith "Wrong paging operation") ops)
    | [] -> Empty
    | ch -> failwith (sprintf "Not a valid transformation: %s" (String.concat "/" ch))

  let fromUrl (s:string) = 
    s.Split([|'/'|], StringSplitOptions.RemoveEmptyEntries) 
    |> chunkBy ((=) "then")
    |> List.ofSeq
    |> List.map parseChunk 
    |> List.rev

  let private formatAgg = function
    | GroupKey -> ["key"]
    | CountAll -> ["count-all"]
    | CountDistinct(f) -> ["count-dist"; f]
    | ReturnUnique(f) -> ["unique"; f]
    | ConcatValues(f) -> ["concat-vals"; f]
    | Sum(f) -> ["sum"; f]

  let toUrl transforms = 
    [ for t in List.rev transforms ->
        match t with
        | DropColumns(columns) -> "drop"::columns
        | SortBy(columns) -> "sort"::(List.collect (fun (c, o) -> [c; (if o = Ascending then "asc" else "desc")]) columns)
        | GroupBy(fld, aggs) -> "group"::fld::(List.collect formatAgg aggs)
        | Paging(pgid, ops) -> "page"::pgid::(List.map (function Take _ -> "take" | Skip _ -> "skip") ops)
        | Empty -> [] ]
    |> List.mapi (fun i l -> if i = 0 then l else "then"::l)
    |> List.concat
    |> String.concat "/"

  let toReturns id tfs = 
    MemberQuery.Returns("nested", "/pivot/" + id + "/" + toUrl tfs, JsonValue.Null)

(*
let test1 =
  [ Transformation.Empty    
    Transformation.DropColumns ["a"; "b" ]
    Transformation.GroupBy("g", [Aggregation.Sum "s"; Aggregation.GroupKey; Aggregation.CountDistinct "d"]) ]

test1 |> Transform.toUrl |> Transform.fromUrl = test1
*)

// ----------------------------------------------------------------------------
// Agent for keeping state (we remember fields of records we transform)
// ----------------------------------------------------------------------------

type StoredFields = 
  { Fields : (string * TypeInfo.Field) list }

type Message = 
  | StoreFields of string * StoredFields * AsyncReplyChannel<string>
  | LookupId of string * AsyncReplyChannel<StoredFields option>

let agent = MailboxProcessor.Start (fun inbox ->
  let rec loop memberToId idToFields = async {
    let! msg = inbox.Receive()
    match msg with
    | LookupId(id, repl) ->
        repl.Reply(Map.tryFind id idToFields)
        return! loop memberToId idToFields
    | StoreFields(membr, fields, repl) ->
        match Map.tryFind membr memberToId with
        | Some id -> 
            repl.Reply(id)
            return! loop memberToId idToFields
        | None ->
            let id = sprintf "inject-%d" (memberToId |> Seq.length)
            repl.Reply(id)
            return! loop (Map.add membr id memberToId) (Map.add id fields idToFields) }
  loop Map.empty Map.empty )

let storeFields membr fields = 
  agent.PostAndReply(fun ch -> StoreFields(membr, fields, ch))

let getFields id = 
  agent.PostAndReply(fun ch -> LookupId(id, ch))

// ----------------------------------------------------------------------------
//
// ----------------------------------------------------------------------------

let singleTransformFields fields = function
  | Empty -> fields
  | SortBy _ -> fields
  | Paging _ -> fields
  | DropColumns(drop) ->
      let dropped = set drop
      fields |> List.filter (fst >> dropped.Contains >> not)
  | GroupBy(fld, aggs) ->
      let oldFields = dict fields
      aggs 
      |> List.map (function
         | GroupKey -> oldFields.[fld]
         | ReturnUnique fld
         | ConcatValues fld
         | Sum fld -> oldFields.[fld]
         | CountAll -> TypeInfo.Field("count", JsonValue.String "int") 
         | CountDistinct fld -> TypeInfo.Field(oldFields.[fld].Name, JsonValue.String "int"))
      |> List.mapi (fun i fld -> sprintf "field_%d" i, fld)

let transformFields fields tfs = 
  { Fields = tfs |> List.fold singleTransformFields fields.Fields }

let mapTransformFields f = function
  | Paging(pgid, ops) -> Paging(pgid, ops)
  | DropColumns cols -> DropColumns (List.map f cols)
  | SortBy cols -> SortBy (List.map (fun (fld, o) -> (f fld, o)) cols)
  | GroupBy(fld, aggs) -> 
      let aggs = aggs |> List.map (function
          | CountDistinct fld -> CountDistinct(f fld)
          | ReturnUnique fld -> ReturnUnique(f fld)
          | ConcatValues fld -> ConcatValues(f fld)
          | Sum fld -> Sum(f fld)
          | agg -> agg)
      GroupBy(f fld, aggs)
  | Empty -> Empty

let renameFields fields tfs = 
  let rec loop fields acc = function
    | [] -> List.rev acc
    | tf::tfs ->
        let rename f = fields |> Seq.pick (fun (fldid, fld:TypeInfo.Field) -> 
          if fldid = f then Some fld.Name else None)
        let fields = singleTransformFields fields tf
        loop fields ((mapTransformFields rename tf)::acc) tfs
  loop fields [] (List.rev tfs) |> List.rev

// ----------------------------------------------------------------------------
//
// ----------------------------------------------------------------------------

let makeMethod id name tfs trace pars (doc:string) = 
  MemberQuery.Root
    ( name, Transform.toReturns id tfs, Array.ofSeq trace, 
      [| for n, t in pars -> MemberQuery.Parameter(n, JsonValue.String t) |], 
      MemberQuery.StringOrDocumentation(doc) )

let makeProperty id name tfs trace (doc:string) = 
  makeMethod id name tfs trace [] doc

let membersOk (s:seq<MemberQuery.Root>) = 
  JsonValue.Array([| for a in s -> a.JsonValue |]).ToString() |> Successful.OK

let isSummable (js:JsonValue) =
  TypeInfo.Root(js).String = Some "int" ||
  TypeInfo.Root(js).String = Some "float"

let isConcatenable (js:JsonValue) =
  TypeInfo.Root(js).String = Some "string"  

let makeRecordSeq fields = 
  let recd = TypeInfo.Record("record", Array.ofSeq fields, [||])
  TypeInfo.Record("seq", [||], [| recd.JsonValue |]).JsonValue

let makeDataMember name tfs injectid (doc:string) = 
  let fields = (getFields injectid).Value.Fields
  let dataTyp = fields |> List.map snd |> makeRecordSeq
  let dataRet = MemberQuery.Returns("primitive", "/pivot/data", dataTyp)
  let fieldsLookup = dict fields
  let url = tfs |> renameFields fields |> Transform.toUrl  
  let trace = [| "pivot-tfs=" + url |]
  MemberQuery.Root("get the data", dataRet, trace, [||], MemberQuery.StringOrDocumentation(doc))            





(*
Todo clean below
*)
let (|ComplexType|_|) (js:JsonValue) =
  TypeInfo.Root(js).Record
  |> Option.map (fun r -> r.Name, [ for p in r.Params -> p.JsonValue ], r.Fields)

let (|RecordSeqMember|_|) (membr:MemberQuery.Root) =
  if membr.Returns.Kind = "primitive" then
    match membr.Returns.Type.JsonValue with
    | ComplexType("seq", [ComplexType("record", _, fields)], _) -> Some(fields)
    | _ -> None
  else None

let (|SplitString|) (by:char) (s:string) =
  s.Split([| by |], StringSplitOptions.RemoveEmptyEntries) |> List.ofSeq

let concatUrl parts = 
  String.concat "/" [ for (p:string) in parts -> p.Trim('/') ]

let originalRequest source parts ctx = 
  let another = concatUrl (source::parts)
  let body = 
    if ctx.request.rawForm.Length = 0 then None
    else Some(HttpRequestBody.BinaryUpload ctx.request.rawForm)
  Http.AsyncRequestString
    ( another, [], httpMethod = ctx.request.``method``.ToString(), ?body = body )

type TraceValue = { Key : string; Value : string; Params : Map<string, string> }

let (|TryFind|_|) m k = Map.tryFind k m
let (|KeyEqualsValue|_|) (s:string) = 
  let kv = s.Split('=')
  if kv.Length > 0 then Some(kv.[0], String.concat "=" kv.[1 ..])
  else None

let parseTrace items trace =
  let items = Map.ofSeq items
  let rec loop accVals accTrace trace = 
    match trace with
    | (KeyEqualsValue(k & TryFind items n, v))::trace ->
        let pars = List.take n trace |> List.map (function KeyEqualsValue(k, v) -> k, v | _ -> failwith "Expected key value pair") |> Map.ofSeq
        let trace = List.skip n trace
        let value = { Key = k; Value = v; Params = pars }
        loop (value::accVals) accTrace trace
    | t::trace ->
        loop accVals (t::accTrace) trace
    | [] -> accVals, List.rev accTrace |> String.concat "&"
  loop [] [] trace

module Seq = 
  let inline the s = s |> Seq.reduce (fun a b -> if a = b then a else failwithf "Not unique: %A <> %A" a b)

// ----------------------------------------------------------------------------
// Filtering or grouping data at runtime
// ----------------------------------------------------------------------------

let inline pickField name obj = 
  Array.pick (fun (n, v) -> if n = name then Some v else None) obj

let applyAggregation (kfld, kval) group = function
 | GroupKey -> kfld, kval
 | CountAll -> "count", JsonValue.Number(group |> Seq.length |> decimal)
 | CountDistinct(fld) -> fld, JsonValue.Number(group |> Seq.distinctBy (pickField fld) |> Seq.length |> decimal)
 | ReturnUnique(fld) -> fld, group |> Seq.map (pickField fld) |> Seq.the
 | ConcatValues(fld) -> fld, group |> Seq.map(fun obj -> (pickField fld obj).AsString()) |> Seq.distinct |> String.concat ", " |> JsonValue.String
 | Sum(fld) -> fld, group |> Seq.sumBy (fun obj -> (pickField fld obj).AsDecimal()) |> JsonValue.Number

let compareFields o1 o2 (fld, order) = 
  let reverse = if order = Descending then -1 else 1
  match pickField fld o1, pickField fld o2 with
  | JsonValue.Number d1, JsonValue.Number d2 -> reverse * compare d1 d2
  | JsonValue.Float f1, JsonValue.Float f2 -> reverse * compare f1 f2
  | JsonValue.Number d1, JsonValue.Float f2 -> reverse * compare (float d1) f2
  | JsonValue.Float f1, JsonValue.Number d2 -> reverse * compare f1 (float d2)
  | JsonValue.Boolean b1, JsonValue.Boolean b2 -> reverse * compare b1 b2
  | JsonValue.String s1, JsonValue.String s2 -> reverse * compare s1 s2
  | _ -> failwith "Cannot compare values"

let transformJson lookupRuntimeArg (objs:seq<(string*JsonValue)[]>) = function
  | Empty -> objs
  | Paging(pgid, pgops) ->
      pgops |> Seq.fold (fun objs -> function
        | Take -> objs |> Seq.take (lookupRuntimeArg pgid "take" "count" |> int)
        | Skip -> objs |> Seq.skip (lookupRuntimeArg pgid "skip" "count" |> int)) objs
  | DropColumns(flds) ->
      let dropped = set flds
      objs |> Seq.map (fun obj ->
        obj |> Array.filter (fst >> dropped.Contains >> not))
  | SortBy(flds) ->
      let flds = List.rev flds
      objs |> Seq.sortWith (fun o1 o2 ->
        let optRes = flds |> List.map (compareFields o1 o2) |> List.skipWhile ((=) 0) |> List.tryHead
        defaultArg optRes 0)
  | GroupBy(fld, aggs) ->
      let aggs = List.rev aggs
      objs 
      |> Seq.groupBy (pickField fld)
      |> Seq.map (fun (kval, group) ->
        aggs 
        |> List.map (applyAggregation (fld, kval) group)
        |> Array.ofSeq )

let mutable cache = Map.empty

let handleDataRequest source ctx = async {
  let trace = (Utils.ASCII.toString ctx.request.rawForm).Split('&') |> List.ofSeq
  let traceParams = ["pivot-source", 0; "pivot-tfs", 0; "pivot-take", 1; "pivot-skip", 1 ]
  let parsed, sourceTrace = trace |> parseTrace traceParams

  let traceLookup k = parsed |> Seq.pick (fun ti -> if ti.Key = k then Some ti.Value else None)
  let sourceEndpoint = traceLookup "pivot-source"
  let tfs = traceLookup "pivot-tfs" |> Transform.fromUrl
  let lookupRuntimeArg pgid op pname = 
    parsed |> Seq.pick (fun ti -> 
      if ti.Key = "pivot-" + op && ti.Value = pgid then Some ti.Params.[pname] else None)
  
  printfn "Trace: %A\nParsed: %A\nRest: %A\n Transform: %A" trace parsed sourceTrace tfs
    
  printfn "Requesting data"
  let! data = 
    match cache.TryFind( (source, sourceEndpoint, sourceTrace) ) with
    | Some res -> async { return res }
    | None -> async {
        let! data = 
          Http.AsyncRequestString
            ( concatUrl [source; sourceEndpoint], httpMethod = "POST", 
              body = HttpRequestBody.TextRequest sourceTrace )
        let data = JsonValue.Parse(data).AsArray() 
        cache <- Map.add (source, sourceEndpoint, sourceTrace) data cache
        return data }

  printfn "Transforming data"
  let recds = data |> Seq.map (function JsonValue.Record r -> r | _ -> failwith "Expected array of records")
  let data = tfs |> List.rev |> List.fold (transformJson lookupRuntimeArg) recds
  let res = JsonValue.Array(data |> Seq.map JsonValue.Record |> Array.ofSeq).ToString()

  printfn "Returning data"
  return! Successful.OK res ctx }

// ----------------------------------------------------------------------------
// Transformations
// ----------------------------------------------------------------------------

let handlePagingRequest injectid { Fields = fields } rest pgid ops =
  let takeDoc = "Take the specified number of rows and drop the rest"
  let takeMemb = makeMethod injectid ("take") (Empty::Paging(pgid, List.rev (Take::ops))::rest) ["pivot-take=" + pgid] ["count", "int"] takeDoc
  let skipDoc = "Skip the specified number of rows and keep the rest"
  let skipMemb = makeMethod injectid ("skip") (Paging(pgid, Skip::ops)::rest) ["pivot-skip=" + pgid] ["count", "int"] takeDoc
  let thenMemb = makeProperty injectid "then" (Empty::Paging(pgid, List.rev ops)::rest) [] "Return the data"
  ( match ops with
    | [] -> [skipMemb; takeMemb]
    | [Skip _] -> [takeMemb; thenMemb]
    | _ -> failwith "handlePagingRequest: Shold not happen" ) |> membersOk

let handleDropRequest injectid { Fields = fields } rest dropped = 
  let droppedFields = set dropped
  [ yield makeProperty injectid "then" (Empty::DropColumns(dropped)::rest) [] "Return the data"
    for fldid, field in fields do
      if not (droppedFields.Contains fldid) then
        let doc = sprintf "Removes the field '%s' from the returned data set" field.Name
        yield makeProperty injectid ("drop " + field.Name) (DropColumns(fldid::dropped)::rest) [] doc ]
  |> membersOk    

let handleSortRequest injectid { Fields = fields } rest keys = 
  let usedKeys = set (List.map fst keys)
  [ yield makeProperty injectid "then" (Empty::SortBy(keys)::rest) [] "Return the data"
    for fldid, field in fields do
      if not (usedKeys.Contains fldid) then
        let doc = sprintf "Use the field '%s' as the next sorting keys" field.Name
        let prefix = if keys = [] then "by " else "and by "
        yield makeProperty injectid (prefix + field.Name) (SortBy((fldid, Ascending)::keys)::rest) [] doc 
        yield makeProperty injectid (prefix + field.Name + " descending") (SortBy((fldid, Descending)::keys)::rest) [] doc ]
  |> membersOk    

let handleGroupRequest injectid { Fields = fields } rest = 
  [ for fldid, field in fields ->
      let doc = sprintf "Groupd data by field '%s' and then aggregate groups" field.Name
      makeProperty injectid ("by " + field.Name) (GroupBy(fldid, [GroupKey])::rest) [] doc ]
  |> membersOk  

let handleGroupAggRequest injectid { Fields = fields } rest fld aggs =
  let containsCountAll = aggs |> Seq.exists ((=) CountAll)
  let containsField fld = aggs |> Seq.exists (function 
    | CountDistinct f | ReturnUnique f | ConcatValues f | Sum f -> f = fld | CountAll | GroupKey -> false)

  let makeAggMember name agg doc = 
    makeProperty injectid name (GroupBy(fld,agg::aggs)::rest) [] doc

  [ yield makeProperty injectid "then" (Empty::GroupBy(fld, aggs)::rest) [] "Get data or perform another transformation"
    if not containsCountAll then 
      yield makeAggMember "count all" CountAll "Count the number of items in the group"
    for fldid, fld in fields do
      if not (containsField fldid) then
        yield makeAggMember ("count distinct " + fld.Name) (CountDistinct fldid) 
                "Count the number of distinct values of the field"
        yield makeAggMember ("return unique " + fld.Name) (ReturnUnique fldid) 
                "Add the value of the field assuming it is unique in the group"
        if isConcatenable fld.Type.JsonValue then
          yield makeAggMember ("concatenate values of " + fld.Name) (ConcatValues fldid)
                  "Concatenate all values of the field"
        if isSummable fld.Type.JsonValue then
          yield makeAggMember ("sum " + fld.Name) (Sum fldid)
                  "Sum the values of the field in the group" ]
  |> membersOk  
  
let handleProxyRequest source local ctx = async { 
  let! data = originalRequest source local ctx       
  let transformed = 
    if ctx.request.``method`` = HttpMethod.GET then
      let members = 
        MemberQuery.Parse(data)
        |> Array.map (fun membr ->
            match membr with
            | RecordSeqMember fields ->
                let id = 
                  storeFields 
                    (ctx.request.url.ToString() + "\n" + membr.Name) 
                    { Fields = fields |> Seq.mapi (fun i fld -> sprintf "a_%d" i, fld) |> List.ofSeq }
                let trace = Array.append [| "pivot-source=" + membr.Returns.Endpoint |] membr.Trace
                (makeProperty id membr.Name [Empty] trace "Get data and perform pivoting operations on it.").JsonValue
            | _ -> membr.JsonValue)
        |> JsonValue.Array
      members.ToString()
    else data              
  return! Successful.OK transformed ctx }

// ----------------------------------------------------------------------------
// Server
// ----------------------------------------------------------------------------

let withSource f ctx =
  let cookies = 
    ctx.request.headers |> Seq.tryFind (fun (h, _) -> h.ToLower() = "x-cookie") 
    |> Option.map (snd >> Cookie.parseCookies)
    |> Option.toList |> List.concat
    |> List.fold (fun cks ck -> Map.add ck.name ck cks) ctx.request.cookies
  match cookies.TryFind("source") with
  | Some c -> f c.value ctx
  | _ -> RequestErrors.BAD_REQUEST "Cookie 'source' was missing." ctx

let pivotRequest f = 
  pathScan "/pivot/%s/%s" (fun (injectid, rest) ->
    match getFields injectid with
    | Some fields -> 
        let tfs = Transform.fromUrl rest
        let current, rest = match tfs with c::r -> c, r | [] -> Empty, []
        let fields = transformFields fields (List.rev rest)
        f injectid fields rest current
    | _ -> RequestErrors.BAD_REQUEST "Ibjected path does not have valid id")

let app = withSource (fun source ->
  choose [
    path "/pivot/data" >=> handleDataRequest source
    pivotRequest (fun injectid fields rest -> function
      // Starting a new pivoting operation
      | Empty ->
          let pgid = rest |> Seq.sumBy (function Paging _ -> 1 | _ -> 0) |> sprintf "pgid-%d"  
          [ makeProperty injectid "group data" (GroupBy("!", [])::rest) [] "Lets you perform pivot table aggregations."
            makeProperty injectid "sort data" (SortBy([])::rest) [] "Lets you sort data in the table by given column(s)."
            makeProperty injectid "filter columns" (DropColumns([])::rest) [] "Lets you remove some of the columns from the table." 
            makeProperty injectid "paging" (Paging(pgid, [])::rest) [] "Take a number of rows or skip a number of rows." 
            makeDataMember "get the data" rest injectid "Returns the transformed data" ]
          |> membersOk    
      // 
      | Paging(pgid, ops) ->
          handlePagingRequest injectid fields rest pgid ops
      | SortBy(keys) ->
          handleSortRequest injectid fields rest keys
      | DropColumns(dropped) ->
          handleDropRequest injectid fields rest dropped
      | GroupBy("!", []) ->
          handleGroupRequest injectid fields rest 
      | GroupBy(fld, aggs) ->
          handleGroupAggRequest injectid fields rest fld aggs )
    pathScan "/%s" (fun (SplitString '/' local) -> handleProxyRequest source local)
  ])