﻿namespace ViewModels

open System.IO
open System.ComponentModel
open System.Windows.Forms
open Microsoft.Win32
open Microsoft.FSharp.Linq.NullableOperators
open CommandUtils
open Convert
open Views

type ViewModelBase() =
    let propertyChangedEvent = Event<_, _>()
    
    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member this.PropertyChanged = propertyChangedEvent.Publish
    
    member this.NotifyPropertyChanged(name : string) =
        propertyChangedEvent.Trigger(this, PropertyChangedEventArgs(name))

type MainViewModel(window : MainWindow) as self =
    inherit ViewModelBase()

    let mutable inputFilePath = ""
    let mutable outputDirectory = ""
    member __.InputFilePath with get() = inputFilePath
                            and  set v = inputFilePath <- v
                                         self.NotifyPropertyChanged("InputFilePath")

    member __.OutputDirectory with get() = outputDirectory
                              and  set v = outputDirectory <- v
                                           self.NotifyPropertyChanged("OutputDirectory")
    
    member __.BrowseInputFileCommand = functionCommand(fun () ->
        let dialog = OpenFileDialog()
        let result = dialog.ShowDialog(window)
        if result ?= true then
            self.InputFilePath <- dialog.FileName)
    
    member __.BrowseOutputDirectoryCommand = functionCommand(fun () ->
        use dialog = new FolderBrowserDialog()
        let result = dialog.ShowDialog()
        if result = DialogResult.OK then
            self.OutputDirectory <- dialog.SelectedPath)
    
    member __.ConvertCommand = functionCommand(fun () ->
        try
            if not (File.Exists(inputFilePath))
            then failwith "Input file is not found."
            if not (Directory.Exists(outputDirectory))
            then failwith "Output directory is not found."

            let outputFileName = Path.ChangeExtension(Path.GetFileName(inputFilePath), "wav")
            let outputFilePath = Path.Combine(outputDirectory, outputFileName)
            convertMidiToWave inputFilePath outputFilePath
        with err ->
            MessageBox.Show(err.Message, "Conversion failed", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore)
