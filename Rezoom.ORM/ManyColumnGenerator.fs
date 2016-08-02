﻿namespace Rezoom.ORM
open LicenseToCIL
open LicenseToCIL.Stack
open LicenseToCIL.Ops
open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit

type private ManyColumnGenerator
    ( builder
    , column : Column option
    , element : ElementBlueprint
    , conversion : ConversionMethod
    ) =
    inherit EntityReaderColumnGenerator(builder)
    let elementId =
        match element.Shape with
        | Composite { Identity = Some id } -> id
        | _ -> failwith "Unsupported collection type"
    let elemTy = element.Output
    let staticTemplate = Generation.readerTemplateGeneric.MakeGenericType(elemTy)
    let entTemplate = typedefof<_ EntityReaderTemplate>.MakeGenericType(elemTy)
    let elemReaderTy = typedefof<_ EntityReader>.MakeGenericType(elemTy)
    let dictTy = typedefof<Dictionary<_, _>>.MakeGenericType(elementId.Blueprint.Value.Output, elemReaderTy)
    let idTy = elementId.Blueprint.Value.Output
    let idConverter =
        match elementId.Blueprint.Value.Cardinality with
        | One { Shape = Primitive prim } ->
            prim.Converter
        | One _ -> failwith "Composite keys are not supported"
        | Many _ -> failwith "Collections as keys are not supported"
    let mutable entDict = null
    let mutable refReader = null
    let mutable idInfo = null
    override __.DefineConstructor() =
        let name = defaultArg (column |> Option.map (fun c -> c.Name)) "self"
        idInfo <- builder.DefineField("_m_i_" + name, typeof<ColumnInfo>, FieldAttributes.Private)
        entDict <- builder.DefineField("_m_d_" + name, dictTy, FieldAttributes.Private)
        refReader <- builder.DefineField("_m_r_" + name, elemReaderTy, FieldAttributes.Private)
        zero // don't initialize dictionary yet
    override __.DefineProcessColumns() =
        cil {
            let! skip = deflabel
            yield ldarg 1 // column map
            match column with
            | Some column ->
                yield ldstr column.Name
                yield call2 ColumnMap.SubMapMethod
            | None -> ()
            let! sub = deflocal typeof<ColumnMap>
            yield stloc sub
            yield ldloc sub
            yield brfalse's skip
            yield dup
            yield ldloc sub
            yield ldstr elementId.Name
            yield call2 ColumnMap.ColumnMethod
            yield stfld idInfo
            yield cil {
                yield dup // this
                yield call0 (staticTemplate.GetMethod("Template")) // this, template
                yield callvirt1 (entTemplate.GetMethod("CreateReader")) // this, reader
                yield dup // this, reader, reader
                yield ldloc sub // this, reader, reader, submap
                yield callvirt2'void (elemReaderTy.GetMethod("ProcessColumns")) // this, reader
                yield stfld refReader // _
            }
            yield mark skip
        }
    override __.DefineImpartKnowledgeToNext() =
        cil {
            yield ldarg 1
            yield castclass builder
            yield ldarg 0
            yield ldfld idInfo
            yield stfld idInfo

            let! nread = deflabel
            let! exit = deflabel
            yield dup
            yield ldfld refReader
            yield brfalse's nread
            yield cil {
                yield ldarg 1
                yield ldarg 0
                yield ldfld refReader
                yield castclass builder
                yield call0 (staticTemplate.GetMethod("Template"))
                yield call1 (entTemplate.GetMethod("CreateReader"))
                let! loc = deflocal elemReaderTy
                yield dup
                yield stloc loc
                // that, oldReader, newReader
                yield callvirt2'void (elemReaderTy.GetMethod("ImpartKnowledgeToNext"))
                // that
                yield ldloc loc
                yield stfld refReader
                yield br's exit
            }
            yield mark nread
            yield cil {
                yield ldarg 1
                yield ldnull
                yield stfld refReader
            }
            yield mark exit
        }
    override __.DefineRead(_) =
        cil {
            let! skip = deflabel
            yield dup
            yield ldfld refReader
            yield brfalse skip
            yield cil {
                yield ldarg 1 // row
                yield ldarg 0 // row, this
                yield ldfld idInfo // row, colinfo
                yield ldfld (typeof<ColumnInfo>.GetField("Index")) // row, index
                yield callvirt2 (typeof<Row>.GetMethod("IsNull")) // isnull
                yield brtrue skip
                
                yield ldarg 1 // row
                yield ldarg 0 // row, this
                yield ldfld idInfo // row, colinfo
                yield generalize2 idConverter // id
                
                let! id = deflocal idTy
                yield stloc id
                let! entReader = deflocal elemReaderTy
                yield dup
                yield ldfld entDict
                let! hasDict = deflabel
                yield dup
                yield brtrue's hasDict
                yield pop
                yield dup
                yield newobj0 (dictTy.GetConstructor(Type.EmptyTypes))
                yield stfld entDict
                yield dup
                yield ldfld entDict
                yield mark hasDict
                yield ldloc id
                yield ldloca entReader
                yield call3 (dictTy.GetMethod("TryGetValue"))
                let! readRow = deflabel
                yield brtrue's readRow
                
                yield dup
                yield ldfld entDict
                yield ldloc id
                yield call0 (staticTemplate.GetMethod("Template"))
                yield callvirt1 (entTemplate.GetMethod("CreateReader"))
                yield dup
                yield stloc entReader
                yield call3'void (dictTy.GetMethod("Add", [| idTy; elemReaderTy |]))
                yield dup
                yield ldfld refReader
                yield ldloc entReader
                yield callvirt2'void (elemReaderTy.GetMethod("ImpartKnowledgeToNext"))

                yield mark readRow
                yield ldloc entReader
                yield ldarg 1 // row
                yield callvirt2'void (elemReaderTy.GetMethod("Read"))
            }
            yield mark skip
        }
    override __.DefinePush() =
        cil {
            let! ncase = deflabel
            yield ldarg 0
            yield ldfld entDict
            yield dup
            yield brfalse's ncase
            yield call1 (dictTy.GetProperty("Values").GetGetMethod())
            yield generalize conversion
            yield mark ncase
        }
