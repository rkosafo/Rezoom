﻿namespace Rezoom.ORM
open LicenseToCIL
open LicenseToCIL.Stack
open LicenseToCIL.Ops
open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit

type private EntityReaderBuilder =
    {
        Ctor : E S * IL
        ProcessColumns : E S * IL
        ImpartKnowledge : E S * IL
        Read : E S * IL
        ToEntity : E S * IL
    }

type private StaticEntityReaderTemplate =
    static member ColumnGenerator(builder, column) =
        match column.Blueprint.Value.Cardinality with
        | One { Shape = Primitive p } ->
            PrimitiveColumnGenerator(builder, column, p) :> EntityReaderColumnGenerator
        | One { Shape = Composite c } ->
            CompositeColumnGenerator(builder, column, c) :> EntityReaderColumnGenerator
        | Many (element, conversion) ->
            ManyColumnGenerator(builder, Some column, element, conversion) :> EntityReaderColumnGenerator

    static member ImplementReader(builder : TypeBuilder, ty : Type, primitive : Primitive, readerBuilder) =
        let info = builder.DefineField("_i", typeof<ColumnInfo>, FieldAttributes.Private)
        let value = builder.DefineField("_v", ty, FieldAttributes.Private)
        readerBuilder.Ctor ||> ret'void |> ignore
        readerBuilder.ProcessColumns ||>
            cil {
                yield ldarg 0
                yield ldarg 1
                yield call1 ColumnMap.PrimaryColumnMethod
                yield stfld info
                yield ret'void
            } |> ignore
        readerBuilder.ImpartKnowledge ||>
            cil {
                yield ldarg 1
                yield castclass builder
                yield ldarg 0
                yield ldfld info
                yield stfld info
                yield ret'void
            } |> ignore
        readerBuilder.Read ||>
            cil {
                yield ldarg 0
                yield ldarg 1
                yield ldarg 0
                yield ldfld info
                yield generalize2 primitive.Converter
                yield stfld value
                yield ret'void
            } |> ignore
        readerBuilder.ToEntity ||>
            cil {
                yield ldarg 0
                yield ldfld value
                yield ret
            } |> ignore

    static member ImplementReader(builder : TypeBuilder, element : ElementBlueprint, conversion, readerBuilder) =
        let generator = ManyColumnGenerator(builder, None, element, conversion)
        readerBuilder.Ctor ||> 
            cil {
                yield ldarg 0
                yield generator.DefineConstructor()
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.ProcessColumns ||>
            cil {
                yield ldarg 0
                yield generator.DefineProcessColumns()
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.ImpartKnowledge ||>
            cil {
                yield ldarg 0
                yield generator.DefineImpartKnowledgeToNext()
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.Read ||>
            cil {
                let! lbl = deflabel
                yield ldarg 0
                yield generator.DefineRead(lbl)
                yield mark lbl
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.ToEntity ||>
            cil {
                yield generator.DefinePush()
                yield ret
            } |> ignore
            
    static member ImplementReader(builder, composite : Composite, readerBuilder) =
        let columns =
                [| for column in composite.Columns.Values ->
                    column, StaticEntityReaderTemplate.ColumnGenerator(builder, column)
                |]
        readerBuilder.Ctor ||>
            cil {
                yield ldarg 0
                for _, column in columns do
                    yield column.DefineConstructor()
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.ProcessColumns ||>
            cil {
                yield ldarg 0
                for _, column in columns do
                    yield column.DefineProcessColumns()
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.ImpartKnowledge ||>
            cil {
                yield ldarg 0
                for _, column in columns do
                    yield column.DefineImpartKnowledgeToNext()
                yield pop
                yield ret'void
            } |> ignore
        readerBuilder.Read ||>
            cil {
                let! skipOnes = deflabel
                let! skipAll = deflabel
                yield ldarg 0
                let ones, others = columns |> Array.partition (fun (b, _) -> b.Blueprint.Value.IsOne)
                for _, column in ones do
                    yield column.DefineRead(skipOnes)
                yield mark skipOnes
                for _, column in others do
                    yield column.DefineRead(skipAll)
                yield mark skipAll
                yield pop
                yield ret'void
            } |> ignore
        let constructorColumns =
            seq {
                for blue, column in columns do
                    match blue.Setter with
                    | SetConstructorParameter paramInfo ->
                        yield paramInfo.Position, column
                    | _ -> ()
            } |> Seq.sortBy fst |> Seq.map snd
        readerBuilder.ToEntity ||>
            cil {
                for column in constructorColumns do
                    yield column.DefinePush()
                    yield pretend
                yield newobj'x composite.Constructor
                for blue, column in columns do
                    match blue.Setter with
                    | SetField field ->
                        yield dup
                        yield column.DefinePush()
                        yield stfld field
                    | SetProperty prop ->
                        yield dup
                        yield column.DefinePush()
                        yield callvirt2'void (prop.GetSetMethod())
                    | _ -> ()
                yield ret
            } |> ignore
    static member ImplementReader(blueprint : Blueprint, builder : TypeBuilder) =
        let readerTy = typedefof<_ EntityReader>.MakeGenericType(blueprint.Output)
        let methodAttrs = MethodAttributes.Public ||| MethodAttributes.Virtual
        let readerBuilder =
            {
                Ctor =
                    Stack.empty, IL(builder
                        .DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, Type.EmptyTypes)
                        .GetILGenerator())
                ImpartKnowledge =
                    Stack.empty, IL(builder
                        .DefineMethod("ImpartKnowledgeToNext", methodAttrs, typeof<Void>, [| readerTy |])
                        .GetILGenerator())
                ProcessColumns =
                    Stack.empty, IL(builder
                        .DefineMethod("ProcessColumns", methodAttrs, typeof<Void>, [| typeof<ColumnMap> |])
                        .GetILGenerator())
                Read = Stack.empty, IL(builder
                    .DefineMethod("Read", methodAttrs, typeof<Void>, [| typeof<Row> |]).GetILGenerator())
                ToEntity = Stack.empty, IL(builder
                    .DefineMethod("ToEntity", methodAttrs, blueprint.Output, Type.EmptyTypes).GetILGenerator())
            }
        match blueprint.Cardinality with
        | One { Shape = Primitive primitive } ->
            StaticEntityReaderTemplate.ImplementReader(builder, blueprint.Output, primitive, readerBuilder)
        | One { Shape = Composite composite } ->
            StaticEntityReaderTemplate.ImplementReader(builder, composite, readerBuilder)
        | Many (element, conversion) ->
            StaticEntityReaderTemplate.ImplementReader(builder, element, conversion, readerBuilder)
        builder.CreateType()

type ReaderTemplate<'ent>() =
    static let entType = typeof<'ent>
    static let template =
        let moduleBuilder =
            let assembly = AssemblyName("Readers." + entType.Name + "." + Guid.NewGuid().ToString("N"))
            let appDomain = Threading.Thread.GetDomain()
            let assemblyBuilder = appDomain.DefineDynamicAssembly(assembly, AssemblyBuilderAccess.Run)
            assemblyBuilder.DefineDynamicModule(assembly.Name)
        let readerBaseType = typedefof<_ EntityReader>.MakeGenericType(entType)
        let readerType =
            let builder =
                moduleBuilder.DefineType
                    ( entType.Name + "Reader"
                    , TypeAttributes.Public ||| TypeAttributes.AutoClass ||| TypeAttributes.AnsiClass
                    , readerBaseType
                    )
            StaticEntityReaderTemplate.ImplementReader(Blueprint.ofType entType, builder)
        let templateType =
            let builder =
                moduleBuilder.DefineType
                    ( entType.Name + "Template"
                    , TypeAttributes.Public ||| TypeAttributes.AutoClass ||| TypeAttributes.AnsiClass
                    , typedefof<_ EntityReaderTemplate>.MakeGenericType(entType)
                    )
            ignore <| builder.DefineDefaultConstructor(MethodAttributes.Public)
            let meth =
                builder.DefineMethod
                    ( "CreateReader"
                    , MethodAttributes.Public ||| MethodAttributes.Virtual
                    , readerBaseType
                    , Type.EmptyTypes
                    )
            (Stack.empty, IL(meth.GetILGenerator())) ||>
                cil {
                    yield newobj0 (readerType.GetConstructor(Type.EmptyTypes))
                    yield ret
                } |> ignore
            builder.CreateType()
        Activator.CreateInstance(templateType)
        |> Unchecked.unbox : 'ent EntityReaderTemplate
    static member Template() = template