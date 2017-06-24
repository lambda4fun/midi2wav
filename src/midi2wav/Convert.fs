﻿module Convert

open System
open NAudio.Wave
open NAudio.Midi
open NAudio.SoundFont

[<Measure>]
type s

[<Measure>]
type Hz = /s

[<Measure>]
type beat

[<Measure>]
type tick

[<Measure>]
type sample

type Sample = {
    PreLoop  : int16[]
    Loop     : int16[]
    PostLoop : int16[] }

let keyToFrequency key =
    let n = float (key - 69)
    440.0<Hz> * (2.0 ** (n / 12.0))

let convertMidiToWave (soundFontPath : string) (instrumentName : string) (midiFilePath : string) (waveFilePath : string) =
    let midiFile = MidiFile(midiFilePath)
    let track = 0
    let events = midiFile.Events.[track]
    let tempo = 0.5<s/beat> // 500,000 microseconds
    let ticksPerBeat = float midiFile.DeltaTicksPerQuarterNote * 1.0<tick/beat>

    let soundFont = SoundFont(soundFontPath)
    let instrument = soundFont.Instruments |> Seq.find (fun i -> i.Name = instrumentName)
    let sampleRate = 32000<Hz>
    let numChannels = 1
    
    use writer = new WaveFileWriter(waveFilePath, WaveFormat(int sampleRate, numChannels))
    
    let writeSamples (samples : int16[]) (count : int<sample>) =
        writer.WriteSamples(samples, 0, int count)

    let findZoneByKey key =
        instrument.Zones
        |> Array.filter (fun zone ->
            zone.Generators
            |> Array.exists (fun gen -> gen.GeneratorType = GeneratorEnum.SampleID))
        |> Array.find (fun zone ->
            zone.Generators
            |> Array.exists (fun gen ->
                gen.GeneratorType = GeneratorEnum.KeyRange &&
                int gen.LowByteAmount <= key && key <= int gen.HighByteAmount))

    let ticksToSamples (ticks : int<tick>) =
        let noteLength = float ticks * tempo / ticksPerBeat
        int (noteLength * float sampleRate) * 1<sample>
        
    let recode (event : NoteOnEvent) ticks =
        let sampleCount = ticksToSamples ticks
        let key = event.NoteNumber
        let zone = findZoneByKey key
        let generators = zone.Generators
        let sampleId = generators |> Array.find (fun gen -> gen.GeneratorType = GeneratorEnum.SampleID)
        let sampleHeader = sampleId.SampleHeader
        let mutable startIndex     = int sampleHeader.Start * 1<sample>
        let mutable startLoopIndex = int sampleHeader.StartLoop * 1<sample>
        let mutable endLoopIndex   = int sampleHeader.EndLoop * 1<sample>
        let mutable endIndex       = int sampleHeader.End * 1<sample>
        let mutable rootKey        = int sampleHeader.OriginalPitch

        for gen in generators do
            let offset       index = index + int gen.Int16Amount * 1<sample>
            let coarseOffset index = index + int gen.Int16Amount * 32768<sample>

            match gen.GeneratorType with
            | GeneratorEnum.OverridingRootKey ->
                if 0s <= gen.Int16Amount && gen.Int16Amount <= 127s then
                    rootKey <- int gen.Int16Amount
            | GeneratorEnum.StartAddressOffset ->
                startIndex     <- offset startIndex
            | GeneratorEnum.EndAddressOffset ->
                endIndex       <- offset endIndex
            | GeneratorEnum.StartLoopAddressOffset ->
                startLoopIndex <- offset startLoopIndex
            | GeneratorEnum.EndLoopAddressOffset ->
                endLoopIndex   <- offset endLoopIndex
            | GeneratorEnum.StartAddressCoarseOffset ->
                startIndex     <- coarseOffset startIndex
            | GeneratorEnum.EndAddressCoarseOffset ->
                endIndex       <- coarseOffset endIndex
            | GeneratorEnum.StartLoopAddressCoarseOffset ->
                startLoopIndex <- coarseOffset startLoopIndex
            | GeneratorEnum.EndLoopAddressCoarseOffset ->
                endLoopIndex   <- coarseOffset endLoopIndex
            | _ -> ()
        
        let frequency = keyToFrequency key
        let rootFrequency = keyToFrequency rootKey
        let delta = frequency / rootFrequency
        
        let toInt16s (bytes : byte[]) =
            bytes
            |> Seq.chunkBySize 2
            |> Seq.map (fun pair -> BitConverter.ToInt16(pair, 0))
            |> Seq.toArray
        
        let interpolate (src : int16[]) delta =
            let dst = Array.zeroCreate (int (float src.Length / delta))
            ({ 0 .. dst.Length-1 }, { 0.0 .. delta .. float (src.Length-1) })
            ||> Seq.iter2 (fun dstIndex srcIndex ->
                let r1 = srcIndex % 1.0
                let r2 = 1.0 - r1
                let src = (float src.[int srcIndex] * r1) + (float src.[int (ceil srcIndex)] * r2)
                dst.[dstIndex] <- int16 src)
            dst
            
        let toAddress (index : int<sample>) = int index * 2

        let sample = {
            PreLoop  = interpolate (toInt16s soundFont.SampleData.[toAddress startIndex .. (toAddress startLoopIndex)-1]) delta
            Loop     = interpolate (toInt16s soundFont.SampleData.[toAddress startLoopIndex .. (toAddress endLoopIndex)+1]) delta
            PostLoop = interpolate (toInt16s soundFont.SampleData.[toAddress (endLoopIndex+1<sample>) .. (toAddress endIndex)+1]) delta }
        
        let preLoopLength  = min sampleCount (sample.PreLoop.Length * 1<sample>)
        let postLoopLength = max 0<sample> (min (sampleCount - preLoopLength) (sample.PostLoop.Length * 1<sample>))
        let loopLength     = max 0<sample> (min (sampleCount- preLoopLength - postLoopLength) (sample.Loop.Length * 1<sample>))

        writeSamples sample.PreLoop preLoopLength

        if loopLength > 0<sample> then
            for _ in 1 .. (sampleCount / loopLength) do
                writeSamples sample.Loop loopLength
            writeSamples sample.Loop (sampleCount % loopLength)

        writeSamples sample.PostLoop postLoopLength

        printfn "write: note = %s (%d), duration = %d (%d ticks = %d samples)" event.NoteName event.NoteNumber event.NoteLength ticks sampleCount

    let noteOnEvents =
        events
        |> Seq.filter (fun event -> event.CommandCode = MidiCommandCode.NoteOn)
        |> Seq.map (fun event -> event :?> NoteOnEvent)

    let skip (n : int<sample>) =
        writeSamples (Array.zeroCreate<int16> (int n)) n

    let recodeNoteOn (event : NoteOnEvent) length =
        let noteOnLength = event.NoteLength * 1<tick>
        recode event noteOnLength
        skip (ticksToSamples (length - noteOnLength))

    noteOnEvents
    |> Seq.pairwise
    |> Seq.iter (fun (event1, event2) ->
        let noteLength = int (event2.AbsoluteTime - event1.AbsoluteTime) * 1<tick>
        recodeNoteOn event1 noteLength)

    let lastMidiEvent = Seq.last events
    let lastNoteOnEvent = Seq.last noteOnEvents
    let ticks = int (lastMidiEvent.AbsoluteTime - lastNoteOnEvent.AbsoluteTime) * 1<tick>
    recodeNoteOn lastNoteOnEvent ticks
