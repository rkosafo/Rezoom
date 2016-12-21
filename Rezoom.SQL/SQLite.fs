﻿namespace Rezoom.SQL.SQLite
open System
open System.Data
open System.Collections.Generic
open System.Globalization
open Rezoom.SQL
open Rezoom.SQL.Mapping
open Rezoom.SQL.BackendUtilities
open Rezoom.SQL.Translators

type private SQLiteLiteral() =
    inherit DefaultLiteralTranslator()
    override __.BooleanLiteral(t) = CommandText <| if t then "1" else "0"

type private SQLiteExpression(statement : StatementTranslator, indexer) =
    inherit DefaultExprTranslator(statement, indexer)
    let literal = SQLiteLiteral()
    override __.Literal = upcast literal
    override __.TypeName(name) =
        (Seq.singleton << text) <|
            match name with
            | BooleanTypeName
            | IntegerTypeName Integer8
            | IntegerTypeName Integer16
            | IntegerTypeName Integer32
            | IntegerTypeName Integer64 -> "INT"
            | FloatTypeName Float32
            | FloatTypeName Float64 -> "FLOAT"
            | StringTypeName(_) -> "VARCHAR"
            | BinaryTypeName(_) -> "BLOB"
            | DecimalTypeName
            | DateTimeTypeName
            | DateTimeOffsetTypeName -> failwith <| sprintf "Unsupported type ``%A``" name

type private SQLiteStatement(indexer : IParameterIndexer) as this =
    inherit DefaultStatementTranslator(Name("SQLITE"), indexer)
    let expr = SQLiteExpression(this :> StatementTranslator, indexer)
    override __.Expr = upcast expr

module SQLiteFunctions =
    open Rezoom.SQL.FunctionDeclarations
    let private minmax name =
        {   FunctionName = Name(name)
            FixedArguments = [| a' |]
            VariableArgument = None
            Output = a'
            Aggregate =
                function
                | ArgumentWildcard -> None
                | ArgumentList (_, exprs) ->
                    if exprs.Length = 1 then
                        Some { AllowWildcard = false; AllowDistinct = false }
                    else
                        None
            Idempotent = false
        } |> withVarArg a'
    let functions =
        let numeric ty = ty |> constrained NumericTypeClass
        [|  // core functions from https://www.sqlite.org/lang_corefunc.html
            func "abs" [ a' ] a'
            proc "changes" [] int64
            func "char" [] string |> withVarArg integral
            func "coalesce" [ a'; a' ] a' |> withVarArg a'
            func "glob" [ string; string ] boolean
            func "hex" [ binary ] string
            func "ifnull" [ a'; a' ] a'
            func "instr" [ a' |> constrained StringishTypeClass; a' ] int64
            proc "last_insert_rowid" [] int64
            func "length" [ a' |> constrained StringishTypeClass ] int64
            func "like" [ string; string ] boolean |> withOptArg string
            func "likelihood" [ boolean; float64 ] boolean
            func "likely" [ boolean ] boolean
            // no load_extension
            func "lower" [ string ] string
            func "ltrim" [ string ] string |> withOptArg string
            minmax "max"
            minmax "min"
            func "nullif" [ a'; a' ] a'
            func "printf" [ string ] string |> withVarArg any
            func "quote" [ any ] string
            proc "random" [] int64
            proc "randomblob" [] binary
            func "replace" [ string; string; string ] string
            func "round" [ float64 ] float64 |> withOptArg integral
            func "rtrim" [ string ] string |> withOptArg string
            func "soundex" [ string ] string
            func "sqlite_compileoption_get" [ integral ] string
            func "sqlite_compileoption_used" [ string ] boolean
            func "sqlite_source_id" [] string
            func "sqlite_version" [] string
            func "substr" [ string; integral ] string |> withOptArg integral
            proc "total_changes" [] int64
            func "trim" [ string ] string |> withOptArg integral
            func "typeof" [ any ] string
            func "unicode" [ string ] int64
            func "unlikely" [ boolean ] boolean
            func "upper" [ string ] string
            func "zeroblob" [ integral ] binary

            // aggregate functions from https://www.sqlite.org/lang_aggfunc.html
            aggregate "avg" [ a' |> numeric ] float64
            aggregate "count" [ any ] int64 |> withWildcard
            aggregate "group_concat" [ string ] string |> withOptArg (* separator *) string
            aggregate "sum" [ numeric a' ] a'
            aggregate "total" [ numeric a' ] a'

            // date and time functions from https://www.sqlite.org/lang_datefunc.html
            // for now we use strings to represent dates -- maybe should formalize this by using the datetime type
            // even though its underlying representation will be a string
            func "date" [ string ] string |> withVarArg string
            func "time" [ string ] string |> withVarArg string
            func "datetime" [ string ] string |> withVarArg string
            func "julianday" [ string ] string |> withVarArg string
            func "strftime" [ string; string ] string |> withVarArg string
        |] |> mapBy (fun f -> f.FunctionName)

type SQLiteBackend() =
    static let initialModel =
        let main, temp = Name("main"), Name("temp")
        {   Schemas =
                [   Schema.Empty(main)
                    Schema.Empty(temp)
                ] |> List.map (fun s -> s.SchemaName, s) |> Map.ofList
            DefaultSchema = main
            TemporarySchema = temp
            Builtin =
                {   Functions = SQLiteFunctions.functions
                }
        }
    interface IBackend with
        member this.MigrationBackend = <@ fun conn -> DefaultMigrationBackend(conn) :> Migrations.IMigrationBackend @>
        member this.InitialModel = initialModel
        member this.ParameterTransform(columnType) = ParameterTransform.Default(columnType)
        member this.ToCommandFragments(indexer, stmts) =
            let translator = SQLiteStatement(indexer)
            translator.TotalStatements(stmts)
            |> BackendUtilities.simplifyFragments
            |> ResizeArray
            :> _ IReadOnlyList
       