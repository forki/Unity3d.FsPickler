﻿module internal FsCoreSerializer.BaseFormatters

    open System
    open System.Reflection
    open System.IO
    open System.Collections.Generic
    open System.Collections.Concurrent
    open System.Runtime.Serialization
    open System.Runtime.CompilerServices

    open FsCoreSerializer
    open FsCoreSerializer.Reflection
    open FsCoreSerializer.FormatterUtils

    let primitiveFormatters =
        [   
            mkFormatter FormatterInfo.Atomic false false ignore (fun _ _ -> ())
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadByte()) (fun bw x -> bw.BW.Write x)
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadSByte()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadChar()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadBoolean()) (fun bw x -> bw.BW.Write x)
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadDecimal()) (fun bw x -> bw.BW.Write x)
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadSingle()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadDouble()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadInt16()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadInt32()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadInt64()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadUInt16()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadUInt32()) (fun bw x -> bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadUInt64()) (fun bw x -> bw.BW.Write x) 
        ]


    let valueFormatters =
        [
            mkFormatter FormatterInfo.Atomic false false (fun br -> br.BR.ReadString()) (fun bw x -> bw.BW.Write x)
            mkFormatter FormatterInfo.Atomic false false (fun br -> Guid(br.BR.ReadBytes(16))) (fun bw x -> bw.BW.Write(x.ToByteArray()))
            mkFormatter FormatterInfo.Atomic false false (fun br -> TimeSpan(br.BR.ReadInt64())) (fun bw x -> bw.BW.Write(x.Ticks))
            mkFormatter FormatterInfo.Atomic false false (fun br -> DateTime(br.BR.ReadInt64())) (fun bw x -> bw.BW.Write(x.Ticks)) 
            mkFormatter FormatterInfo.Atomic false true (fun br -> br.BR.ReadBytes(br.BR.ReadInt32())) (fun bw x -> bw.BW.Write x.Length ; bw.BW.Write x) 
            mkFormatter FormatterInfo.Atomic false false (fun r -> System.Numerics.BigInteger(r.BR.ReadBytes(r.BR.ReadInt32())))
                                                         (fun w x -> let bs = x.ToByteArray() in w.BW.Write bs.Length ; w.BW.Write bs)
        ]


    //
    //  Reflection formatters
    //

    let typeFormatter =
        mkFormatter FormatterInfo.ReflectionType true true 
                    (fun r -> TypeFormatter.Read r.BR) 
                    (fun w t -> TypeFormatter.Write w.BW t)

    let assemblyFormatter =
        let writer (w : Writer) (a : Assembly) = w.BW.Write(a.FullName)
        let reader (r : Reader) = Assembly.Load(r.BR.ReadString())

        mkFormatter FormatterInfo.ReflectionType true true reader writer

    let memberInfoFormatter =
        let writer (w : Writer) (m : MemberInfo) =
            write w typeFormatter m.ReflectedType
            match m with
            | :? MethodInfo as m when m.IsGenericMethod && not m.IsGenericMethodDefinition ->
                let gm = m.GetGenericMethodDefinition()
                let ga = m.GetGenericArguments()
                w.BW.Write (gm.ToString())

                w.BW.Write true
                w.BW.Write ga.Length
                for a in ga do write w typeFormatter a
            | _ ->
                w.BW.Write (m.ToString())
                w.BW.Write false

        let reader (r : Reader) =
            let t = read r typeFormatter :?> Type
            let mname = r.BR.ReadString()
            let m = 
                try t.GetMembers(allMembers) |> Array.find (fun m -> m.ToString() = mname)
                with :? KeyNotFoundException ->
                    raise <| new SerializationException(sprintf "Could not deserialize member '%O.%s'" t.Name mname)

            if r.BR.ReadBoolean() then
                let n = r.BR.ReadInt32()
                let ga = Array.zeroCreate<Type> n
                for i = 0 to n - 1 do ga.[i] <- read r typeFormatter :?> Type
                (m :?> MethodInfo).MakeGenericMethod ga :> MemberInfo
            else
                m

        mkFormatter FormatterInfo.ReflectionType true true reader writer

    let typeHandleFormatter =
        mkFormatter FormatterInfo.ReflectionType true true 
                (fun r -> let t = read r typeFormatter :?> Type in t.TypeHandle)
                (fun w th -> write w typeFormatter (Type.GetTypeFromHandle th))

    let fieldHandleFormatter =
        mkFormatter FormatterInfo.ReflectionType true true
                (fun r -> let f = read r memberInfoFormatter :?> FieldInfo in f.FieldHandle)
                (fun w fh -> write w memberInfoFormatter (FieldInfo.GetFieldFromHandle fh))

    let methodHandleFormatter =
        mkFormatter FormatterInfo.ReflectionType true true
                (fun r -> let m = read r memberInfoFormatter :?> MethodInfo in m.MethodHandle)
                (fun w mh -> write w memberInfoFormatter (MethodInfo.GetMethodFromHandle mh))

    let assemblyNameFormatter =
        mkFormatter FormatterInfo.ReflectionType true true
                (fun r -> AssemblyName(r.BR.ReadString()))
                (fun w a -> w.BW.Write a.FullName)

    let delegateFormatter =
        let writer (w : Writer) (dele : System.Delegate) =
            w.WriteObj(typeFormatter, dele.GetType())
            w.WriteObj(memberInfoFormatter, dele.Method)
            if not dele.Method.IsStatic then w.WriteObj dele.Target

        let reader (r : Reader) =
            let ty = r.ReadObj typeFormatter :?> Type
            let meth = r.ReadObj memberInfoFormatter :?> MethodInfo

            if not meth.IsStatic then
                let target = r.ReadObj()
                Delegate.CreateDelegate(ty, target, meth, throwOnBindFailure = true)
            else
                Delegate.CreateDelegate(ty, meth, throwOnBindFailure = true)


        mkFormatter FormatterInfo.Custom true true reader writer

    let dbNullFormatter = mkFormatter FormatterInfo.Custom false true (fun _ -> DBNull.Value) (fun _ _ -> ())

    let reflectionFormatters =
        [ 
            typeFormatter ; assemblyFormatter ; memberInfoFormatter
            typeHandleFormatter ; fieldHandleFormatter ; methodHandleFormatter
            assemblyNameFormatter ; delegateFormatter ; dbNullFormatter
        ]

    //
    //  .NET serialization formatters
    //


    // dummy formatter placeholder for abstract types
    let mkAbstractFormatter (t : Type) =
        {
            Type = t
            TypeInfo = getTypeInfo t
            TypeHash = ObjHeader.getTruncatedHash t

            Write = fun _ _ -> raise <| new NotSupportedException("cannot serialize an abstract type")
            Read = fun _ -> raise <| new NotSupportedException("cannot serialize an abstract type")

            FormatterInfo = FormatterInfo.ReflectionDerived
            UseWithSubtypes = false
            CacheObj = true
        }

    // formatter builder for ISerializable types

    let tryMkISerializableFormatter (t : Type) =
        if typeof<ISerializable>.IsAssignableFrom t then
            match tryGetCtor t [| typeof<SerializationInfo> ; typeof<StreamingContext> |] with
            | None -> None
            | Some ctor ->
                
                if t = typeof<IntPtr> || t = typeof<UIntPtr> then
                    raise <| new SerializationException("Serialization of pointers not supported.")

#if EMIT_IL
                let ctorFunc = FSharpValue.PreComputeConstructor ctor
                let precompute (m : MethodInfo) = FSharpValue.PreComputeMethod(t, m)

                let inline run o (sc : StreamingContext) (fs : (obj * obj [] -> obj) []) =
                    for f in fs do f (o , [| sc :> obj |]) |> ignore
#else
                let inline ctorFunc args = ctor.Invoke args
                let precompute = id

                let inline run o (sc : StreamingContext) (ms : MethodInfo []) =
                    for m in ms do m.Invoke(o, [| sc :> obj |]) |> ignore
#endif

                let allMethods = t.GetMethods(allMembers)
                
                let onSerializing = allMethods |> Array.filter containsAttr<OnSerializingAttribute> |> Array.map precompute
                let onSerialized = allMethods |> Array.filter containsAttr<OnSerializedAttribute> |> Array.map precompute
                let onDeserialized = allMethods |> Array.filter containsAttr<OnDeserializedAttribute> |> Array.map precompute
                let isDeserializationCallback = typeof<IDeserializationCallback>.IsAssignableFrom t

                let writer (w : Writer) (o : obj) =
                    run o w.StreamingContext onSerializing
                    let s = o :?> ISerializable
                    let sI = new SerializationInfo(t, new FormatterConverter())
                    s.GetObjectData(sI, w.StreamingContext)
                    w.BW.Write sI.MemberCount
                    let enum = sI.GetEnumerator()
                    while enum.MoveNext() do
                        w.BW.Write enum.Current.Name
                        w.WriteObj enum.Current.Value

                    run o w.StreamingContext onSerialized

                let reader (r : Reader) =
                    let sI = new SerializationInfo(t, new FormatterConverter())
                    let memberCount = r.BR.ReadInt32()
                    for i = 1 to memberCount do
                        let name = r.BR.ReadString()
                        let v = r.ReadObj ()
                        sI.AddValue(name, v)

                    let o = ctorFunc [| sI :> obj ; r.StreamingContext :> obj |]
                    run o r.StreamingContext onDeserialized
                    if isDeserializationCallback then (o :?> IDeserializationCallback).OnDeserialization null
                    o

                Some {
                    Type = t
                    TypeInfo = getTypeInfo t
                    TypeHash = ObjHeader.getTruncatedHash t

                    Write = writer
                    Read = reader

                    FormatterInfo = FormatterInfo.ISerializable
                    CacheObj = true
                    UseWithSubtypes = false
                }
        else None

    // formatter builder for IFsCoreSerializable types

    let tryMkIFsCoreSerializable (t : Type) =
        if typeof<IFsCoreSerializable>.IsAssignableFrom t then
            match tryGetCtor t [| typeof<Reader> |] with
            | None -> None
            | Some ctor ->
#if EMIT_IL
                let ctorFunc = FSharpValue.PreComputeConstructor ctor
#else
                let inline ctorFunc args = ctor.Invoke args
#endif
    

                Some {
                    Type = t
                    TypeInfo = getTypeInfo t
                    TypeHash = ObjHeader.getTruncatedHash t

                    Write = fun (w : Writer) (o : obj) -> (o :?> IFsCoreSerializable).GetObjectData(w)
                    Read = fun (r : Reader) -> ctorFunc [| r :> obj |]

                    FormatterInfo = FormatterInfo.IFsCoreSerializable
                    UseWithSubtypes = false
                    CacheObj = true
                }
        else None


    // reflection-based formatter derivation

    let mkReflectionFormatter (resolver : Type -> Lazy<Formatter>) (t : Type) =
        if t.IsPrimitive then raise <| new SerializationException(sprintf "could not derive serialization rules for '%s'." t.Name)
        elif t.IsAbstract then mkAbstractFormatter t
        elif not t.IsSerializable then raise <| new SerializationException(sprintf "type '%s' is marked as nonserializable." t.Name) else


        let allMethods = t.GetMethods(allMembers)
        let fields = t.GetFields(fieldBindings) |> Array.filter (not << containsAttr<NonSerializedAttribute>)
        let isDeserializationCallback = typeof<IDeserializationCallback>.IsAssignableFrom t
        let formatters = fields |> Array.map (fun f -> resolver f.FieldType)

#if EMIT_IL
        let precompute (m : MethodInfo) = FSharpValue.PreComputeMethod(t, m)

        let inline run o (sc : StreamingContext) (fs : (obj * obj [] -> obj) []) =
            for f in fs do f (o , [| sc :> obj |]) |> ignore

        let decomposer = FSharpValue.PreComputeFieldReader(t, fields)
        let composer = FSharpValue.PreComputeObjectInitializer(t, fields)
#else
        let precompute = id

        let inline run o (sc : StreamingContext) (ms : MethodInfo []) =
            for m in ms do m.Invoke(o, [| sc :> obj |]) |> ignore

        let decomposer (o : obj) = 
            let fieldVals = Array.zeroCreate<obj> fields.Length
            for i = 0 to fields.Length - 1 do
                fieldVals.[i] <- fields.[i].GetValue o

            fieldVals

        let composer (o : obj, fieldVals : obj []) =
            for i = 0 to fields.Length - 1 do
                fields.[i].SetValue(o, fieldVals.[i])
#endif
        
        let onSerializing = allMethods |> Array.filter containsAttr<OnSerializingAttribute> |> Array.map precompute
        let onSerialized = allMethods |> Array.filter containsAttr<OnSerializedAttribute> |> Array.map precompute
        let onDeserializing = allMethods |> Array.filter containsAttr<OnDeserializingAttribute> |> Array.map precompute
        let onDeserialized = allMethods |> Array.filter containsAttr<OnDeserializedAttribute> |> Array.map precompute
        

        let tyInfo = getTypeInfo t

        let writer (w : Writer) (o : obj) =
            run o w.StreamingContext onSerializing
            let values = decomposer o
            zipWrite w formatters values

            run o w.StreamingContext onSerialized

        let reader (r : Reader) =
            let o = FormatterServices.GetUninitializedObject(t)
            if tyInfo > TypeInfo.Value then r.EarlyRegisterObject o
            run o r.StreamingContext onDeserializing
            let values = zipRead r formatters

            do composer (o, values)

            run o r.StreamingContext onDeserialized
            if isDeserializationCallback then (o :?> IDeserializationCallback).OnDeserialization null
            o
        {
            Type = t
            TypeInfo = tyInfo
            TypeHash = ObjHeader.getTruncatedHash t

            Write = writer
            Read = reader

            FormatterInfo = FormatterInfo.ReflectionDerived
            UseWithSubtypes = false
            CacheObj = true
        }

    // formatter builder for enumerator types

    let mkEnumFormatter (resolver : Type -> Lazy<Formatter>) (t : Type) =
        let ut = Enum.GetUnderlyingType t
        let uf = (resolver ut).Value

        {
            Type = t
            TypeInfo = getTypeInfo t
            TypeHash = ObjHeader.getTruncatedHash t

            Write = uf.Write
            Read = uf.Read

            FormatterInfo = FormatterInfo.ReflectionDerived
            UseWithSubtypes = false
            CacheObj = false
        }