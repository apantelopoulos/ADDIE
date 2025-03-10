﻿module SheetUpdate

open CommonTypes
open Browser
open Elmish
open DrawHelpers
open DrawModelType
open DrawModelType.SymbolT
open DrawModelType.BusWireT
open DrawModelType.SheetT
open SheetUpdateHelpers
open Sheet
open Optics
open CanvasStateAnalyser
open Simulation
open FSharp.Core
open Node

module node = Node.Api


/// Update Function
let update (msg : Msg) (model : Model): Model*Cmd<Msg> =
    /// check things that might not have been correctly completed in the last update and if so do them
    /// Mostly thsi is a hack to deal with the fact that dependent state is held separately rather than
    /// being derived fucntionally from the state it depends on, so it muts be explicitly updated.
    /// TODO: add something to check whether wires need updating

    let postUpdateChecks (model: Model) =
        // Executed every update so performance is important.
        // Since normally state will be correct it is only necessary to make the checking
        // fast.
        let sModel = Optic.get symbol_ model
        sModel.Symbols
        |> (fun sMap ->
                (model,sMap)
                ||> Map.fold (fun model sId sym -> 
                        if Map.containsKey sId model.BoundingBoxes 
                           && sym.Pos = model.BoundingBoxes[sId].TopLeft then
                            model 
                        else
                            Optic.set boundingBoxes_ (Symbol.getBoundingBoxes sModel) model))
        |> ensureCanvasExtendsBeyondScreen
                                
    match msg with
    | Wire (BusWireT.Symbol SymbolT.Msg.UpdateBoundingBoxes) -> 
        // Symbol cannot directly send a message to Sheet box Sheet message type is out of scape. This
        // is used so that a symbol message can be intercepted by sheet and used there.
        model, Cmd.batch [
                Cmd.ofMsg UpdateBoundingBoxes; 
                ]
    | Wire wMsg ->
        let wModel, wCmd = BusWireUpdate.update wMsg model.Wire
        { model with Wire = wModel }, Cmd.map Wire wCmd
    | ToggleGrid ->
        {model with ShowGrid = not model.ShowGrid}, Cmd.none
    | KeyPress DEL ->
        let wiresConnectedToComponents = BusWireUpdateHelpers.getConnectedWireIds model.Wire model.SelectedComponents
        // Ensure there are no duplicate deletions by using a Set
        let wireUnion =
            Set.union (Set.ofList wiresConnectedToComponents) (Set.ofList model.SelectedWires)
            |> Set.toList

        // let inputPorts, outputPorts = BusWire.getPortIdsOfWires model.Wire wireUnion
        { model with SelectedComponents = []; SelectedWires = []; UndoList = appendUndoList model.UndoList model ; RedoList = [] },
        Cmd.batch [ wireCmd (BusWireT.DeleteWires wireUnion) // Delete Wires before components so nothing bad happens
                    symbolCmd (SymbolT.DeleteSymbols model.SelectedComponents)
                    Cmd.ofMsg UpdateBoundingBoxes
                    Cmd.ofMsg CanvasChanged
                  ]
    | KeyPress CtrlS -> // For Demo, Add a new square in upper left corner
        { model with BoundingBoxes = Symbol.getBoundingBoxes model.Wire.Symbol; UndoList = appendUndoList model.UndoList model ; RedoList = []},
        Cmd.batch [ Cmd.ofMsg UpdateBoundingBoxes; symbolCmd SymbolT.SaveSymbols ] // Need to update bounding boxes after adding a symbol.
    | KeyPress AltShiftZ ->
        TimeHelpers.printStats()
        model, Cmd.none
    | KeyPress CtrlC ->
        model,
        Cmd.batch [
            symbolCmd (SymbolT.CopySymbols model.SelectedComponents) // Better to have Symbol keep track of clipboard as symbols can get deleted before pasting.
            wireCmd (BusWireT.CopyWires model.SelectedWires)
        ]
    | KeyPress CtrlV ->
        let newSymbolModel, pastedCompIds = SymbolUpdate.pasteSymbols model.Wire.Symbol model.Wire.Wires model.LastMousePos // Symbol has Copied Symbols stored
        let newBusWireModel, pastedConnIds = BusWireUpdate.pasteWires { model.Wire with Symbol = newSymbolModel } pastedCompIds

        { model with Wire = newBusWireModel
                     SelectedComponents = pastedCompIds
                     SelectedWires = pastedConnIds
                     TmpModel = Some model
                     Action = DragAndDrop },
        Cmd.batch [ Cmd.ofMsg UpdateBoundingBoxes
                    symbolCmd (SymbolT.SelectSymbols []) // Select to unhighlight all other symbols
                    symbolCmd (SymbolT.PasteSymbols pastedCompIds)
                    wireCmd (BusWireT.SelectWires [])
                    wireCmd (BusWireT.ColorWires (pastedConnIds, HighLightColor.Thistle)) ]

    | KeyPress ESC -> // Cancel Pasting Symbols, and other possible actions in the future
        match model.Action with
        | DragAndDrop ->
            { model with SelectedComponents = []
                         SelectedWires = []
                         SnapSymbols = emptySnap
                         SnapSegments = emptySnap
                         Action = Idle },
            Cmd.batch [ symbolCmd (SymbolT.DeleteSymbols model.SelectedComponents)
                        wireCmd (BusWireT.DeleteWires model.SelectedWires)
                        Cmd.ofMsg UpdateBoundingBoxes
                      ]
        | _ -> model, Cmd.none

    | KeyPress CtrlZ ->
        match model.UndoList with
        | [] -> model , Cmd.none
        | prevModel :: lst ->
            let symModel = { prevModel.Wire.Symbol with CopiedSymbols = model.Wire.Symbol.CopiedSymbols }
            let wireModel = { prevModel.Wire with CopiedWires = model.Wire.CopiedWires ; Symbol = symModel}
            { prevModel with Wire = wireModel ; UndoList = lst ; RedoList = model :: model.RedoList ; CurrentKeyPresses = Set.empty } , Cmd.none

    | KeyPress CtrlY ->
        match model.RedoList with
        | [] -> model , Cmd.none
        | newModel :: lst -> { newModel with UndoList = model :: model.UndoList ; RedoList = lst} , Cmd.none

    | KeyPress CtrlA ->
        let symbols = model.Wire.Symbol.Symbols |> Map.toList |> List.map fst
        let wires = model.Wire.Wires |> Map.toList |> List.map fst
        { model with
            SelectedComponents = symbols
            SelectedWires = wires
        } , Cmd.batch [ symbolCmd (SymbolT.SelectSymbols symbols)
                        wireCmd (BusWireT.SelectWires wires) ]

    | KeyPress CtrlW ->
            let model', paras = fitCircuitToWindowParas model
            writeCanvasScroll paras.Scroll
            model', 
            Cmd.batch 
                [
                    Cmd.ofMsg (UpdateScrollPos paras.Scroll)
                    Cmd.ofMsg UpdateBoundingBoxes
                ]
    

    | MouseMsg mMsg -> // Mouse Update Functions can be found above, update function got very messy otherwise
        let mouseAlreadyDown = match model.Action with | MovingPort _ -> true |_ -> false //| ConnectingInput _ | ConnectingOutput _
        match mMsg.Op with
        | Down when mouseAlreadyDown = true -> model, Cmd.none
        | Down -> mDownUpdate model mMsg
        | Drag -> mDragUpdate model mMsg
        | Up -> mUpUpdate model mMsg
        | Move -> mMoveUpdate model mMsg

    | UpdateBoundingBoxes -> 
        let model =
            model
            |> Optic.set boundingBoxes_ (Symbol.getBoundingBoxes model.Wire.Symbol)
            |> Optic.map symbols_ (Map.map (fun sId sym -> Symbol.calcLabelBoundingBox sym))

        model, Cmd.none

    | UpdateSingleBoundingBox compId ->
        match Map.containsKey compId model.BoundingBoxes with
        | true -> 
            {model with 
                BoundingBoxes = model.BoundingBoxes.Add (compId, (Symbol.getBoundingBox model.Wire.Symbol compId))}
            |> Optic.map symbols_ (Map.change compId (Option.map Symbol.calcLabelBoundingBox))               
                , Cmd.none
        | false -> model, Cmd.none

    | UpdateScrollPosFromCanvas dispatch ->
        let model =
            match canvasDiv with
            | None -> model
            | Some el -> 
                let canvas = document.getElementById "Canvas"
                // UpdateScrollPos here is needed to make CheckAutomaticScrolling work properly
                // Possibly UpdateScrollPos must be after view to trigger the next checkAutomaticScrolling
                // When checkAutomaticScrolling is sone in a better way, this could be removed
                dispatch <| UpdateScrollPos {X=canvas.scrollLeft; Y=canvas.scrollTop} 
                {model with ScreenScrollPos = {X= el.scrollLeft; Y = el.scrollTop}}
        model, Cmd.none

 
    | UpdateScrollPos scrollPos ->
        if model.ScrollUpdateIsOutstanding then 
            model, Cmd.none
        else
            let scrollDif = scrollPos - model.ScreenScrollPos
            let newLastScrollingPos =
                {
                 Pos =
                    {
                        X = model.ScrollingLastMousePos.Pos.X + scrollDif.X / model.Zoom
                        Y = model.ScrollingLastMousePos.Pos.Y + scrollDif.Y / model.Zoom
                    }
                 Move = model.ScrollingLastMousePos.Move
                }
            let cmd =
                if model.AutomaticScrolling then
                    Cmd.ofMsg CheckAutomaticScrolling // Also check if there is automatic scrolling to continue
                else
                    Cmd.none
            //Sheet.writeCanvasScroll scrollPos            
            { model with 
                ScreenScrollPos = scrollPos
                ScrollUpdateIsOutstanding = false
                ScrollingLastMousePos = newLastScrollingPos }, 
                cmd

    // Zooming in increases model.Zoom. The centre of the screen will stay centred (if possible)
    | KeyPress ZoomIn ->
        let oldScreenCentre = getVisibleScreenCentre model
        { model with Zoom = min Constants.maxMagnification (model.Zoom*Constants.zoomIncrement) }, 
        Cmd.ofMsg (KeepZoomCentered oldScreenCentre)

    // Zooming out decreases the model.Zoom. The centre of the screen will stay centred (if possible)
    | KeyPress ZoomOut ->
        // get current screen edge coords
        let edge = getScreenEdgeCoords model
        //Check if the new zoom will exceed the canvas width or height
        let newZoom =
            let minXZoom = (edge.Right - edge.Left) / model.CanvasSize
            let minYZoom = (edge.Top - edge.Bottom) / model.CanvasSize
            List.max [model.Zoom / Constants.zoomIncrement; minXZoom; minYZoom]
        let oldScreenCentre = getVisibleScreenCentre model

        { model with Zoom = newZoom }, 
        Cmd.ofMsg (KeepZoomCentered oldScreenCentre)

    | KeepZoomCentered oldScreenCentre ->
        let canvas = document.getElementById "Canvas"
        let newScreenCentre = getVisibleScreenCentre model
        let requiredOffset = oldScreenCentre - newScreenCentre

        // Update screen so that the zoom is centred around the middle of the screen.
        canvas.scrollLeft <- canvas.scrollLeft + requiredOffset.X * model.Zoom
        canvas.scrollTop <- canvas.scrollTop + requiredOffset.Y * model.Zoom
        model, Cmd.none

    | ManualKeyDown key -> // Needed for e.g. Ctrl + C and Ctrl + V as they are not picked up by Electron
        let newPressedKeys = model.CurrentKeyPresses.Add (key.ToUpper()) // Make it fully upper case to remove CAPS dependency
        //printfn $"Keys={newPressedKeys}, Key={key}"
        let newCmd =
            match Set.contains "CONTROL" newPressedKeys || Set.contains "META" newPressedKeys with
            | true ->
                if Set.contains "C" newPressedKeys then
                    Cmd.ofMsg (KeyPress CtrlC)
                elif Set.contains "V" newPressedKeys then
                    Cmd.ofMsg (KeyPress CtrlV)
                elif Set.contains "A" newPressedKeys then
                    Cmd.ofMsg (KeyPress CtrlA)
                elif Set.contains "W" newPressedKeys then
                    Cmd.ofMsg (KeyPress CtrlW)
                else
                    Cmd.none
            | false -> Cmd.none

        { model with CurrentKeyPresses = newPressedKeys }, newCmd

    | ManualKeyUp key -> 
        { model with CurrentKeyPresses = model.CurrentKeyPresses.Remove (key.ToUpper()) }, Cmd.none

    | CheckAutomaticScrolling ->
        let canvas = document.getElementById "Canvas"
        let wholeApp = document.getElementById "WholeApp"
        let rightSelection = document.getElementById "RightSelection"

        let leftScreenEdge = canvas.scrollLeft
        let rightScreenEdge = leftScreenEdge + wholeApp.clientWidth - rightSelection.clientWidth
        let upperScreenEdge = canvas.scrollTop
        let lowerScreenEdge = upperScreenEdge + canvas.clientHeight
        let mPosX = model.ScrollingLastMousePos.Pos.X * model.Zoom // Un-compensate for zoom as we want raw distance from mouse to edge screen
        let mPosY = model.ScrollingLastMousePos.Pos.Y * model.Zoom // Un-compensate for zoom as we want raw distance from mouse to edge screen
        let mMovX = model.ScrollingLastMousePos.Move.X
        let mMovY = model.ScrollingLastMousePos.Move.Y
        /// no scrolling if too far from edge, or if moving away from edge
        let checkForAutomaticScrolling1D (edge: float) (mPos: float) (mMov: float) =
            let scrollMargin = 100.0
            let scrollSpeed = 10.0
            let edgeDistance = abs (edge - mPos)

            if edgeDistance < scrollMargin && mMov >= -0.0000001 // just in case there are FP rounding errors
            then scrollSpeed * (scrollMargin - edgeDistance) / scrollMargin // Speed should be faster the closer the mouse is to the screen edge
            else 0.0

        canvas.scrollLeft <- canvas.scrollLeft - (checkForAutomaticScrolling1D leftScreenEdge mPosX -mMovX) // Check left-screen edge
        canvas.scrollLeft <- canvas.scrollLeft + (checkForAutomaticScrolling1D rightScreenEdge mPosX mMovX) // Check right-screen edge
        canvas.scrollTop <- canvas.scrollTop - (checkForAutomaticScrolling1D upperScreenEdge mPosY -mMovY) // Check upper-screen edge
        canvas.scrollTop <- canvas.scrollTop + (checkForAutomaticScrolling1D lowerScreenEdge mPosY mMovY) // Check lower-screen edge
        let xDiff = canvas.scrollLeft - leftScreenEdge
        let yDiff = canvas.scrollTop - upperScreenEdge

        if xDiff <> 0.0 || yDiff <> 0.0 then // Did any automatic scrolling happen?
            let newMPos = { X = model.LastMousePos.X + xDiff / model.Zoom ; Y = model.LastMousePos.Y + yDiff / model.Zoom }
            // Need to update mouse movement as well since the scrolling moves the mouse relative to the canvas, but no actual mouse movement will be detected.
            // E.g. a moving symbol should stick to the mouse as the automatic scrolling happens and not lag behind.
            let zero = {X=0.;Y=0.}
            let defaultMsg = { Pos = newMPos; Op = Move;  ScreenMovement = zero; ScreenPage=zero; ShiftKeyDown=false}
            let outputModel, outputCmd =
                match model.Action with
                | DragAndDrop ->
                    mMoveUpdate { model with AutomaticScrolling = true } {defaultMsg with Op = Move}                             
                | MovingSymbols | Selecting -> //| ConnectingInput _ | ConnectingOutput _
                    mDragUpdate { model with AutomaticScrolling = true } {defaultMsg with Op = Drag}                               
                | _ ->
                    { model with AutomaticScrolling = true }, Cmd.none
            let notAutomaticScrolling msg = match msg with | CheckAutomaticScrolling -> false | _ -> true
            // Don't want to go into an infinite loop (program would crash), don't check for automatic scrolling immediately (let it be handled by OnScroll listener).
            let filteredOutputCmd = Cmd.map (fun msg -> if notAutomaticScrolling msg then msg else DoNothing) outputCmd
            // keep model ScrollPos uptodate with real scrolling position
            let outputModel = {outputModel with ScreenScrollPos = {X = canvas.scrollLeft; Y = canvas.scrollTop}}

            outputModel, filteredOutputCmd
        else
            { model with AutomaticScrolling = false}, Cmd.none

    | Arrangement arrange ->
        arrangeSymbols arrange model

    | RotateLabels ->
        rotateSelectedLabelsClockwise model
    
    | Rotate rotation ->
        model,
        Cmd.batch [
            symbolCmd (SymbolT.RotateLeft(model.SelectedComponents, rotation)) // Better to have Symbol keep track of clipboard as symbols can get deleted before pasting.
            wireCmd (BusWireT.UpdateConnectedWires model.SelectedComponents)
            Cmd.ofMsg SheetT.UpdateBoundingBoxes
            Cmd.ofMsg CanvasChanged
        ]

    | Flip orientation ->
        model,
        Cmd.batch [
            symbolCmd (SymbolT.Flip(model.SelectedComponents,orientation)) // Better to have Symbol keep track of clipboard as symbols can get deleted before pasting.
            wireCmd (BusWireT.UpdateConnectedWires model.SelectedComponents)
            Cmd.ofMsg SheetT.UpdateBoundingBoxes
        ]
    | SaveSymbols ->
        model, symbolCmd SymbolT.SaveSymbols

    | WireType Jump ->
        let wires = model.Wire.Wires |> Map.toList |> List.map fst
        model,
        Cmd.batch [
            wireCmd (BusWireT.UpdateWireDisplayType BusWireT.Jump)
            wireCmd (BusWireT.MakeJumps wires)
        ]

    | WireType Radiussed ->
        let wires = model.Wire.Wires |> Map.toList |> List.map fst
        model,
        Cmd.batch [
            wireCmd (BusWireT.UpdateWireDisplayType BusWireT.Radial)
            wireCmd (BusWireT.MakeJumps wires)
        ]
       
    | WireType Modern ->
        let wires = model.Wire.Wires |> Map.toList |> List.map fst
        model,
        Cmd.batch [
            wireCmd (BusWireT.UpdateWireDisplayType BusWireT.Modern)
            wireCmd (BusWireT.MakeJumps wires)
        ]
                
    // ---------------------------- Addie Messages ---------------------------- //

    | InitialiseCreateComponent (ldcs, compType, lbl) ->
        { model with Action = (InitialisedCreateComponent (ldcs, compType, lbl)) ; TmpModel = Some model}, Cmd.none
    | FlushCommandStack -> { model with UndoList = []; RedoList = []; TmpModel = None }, Cmd.none
    | ResetModel ->
        { model with
            BoundingBoxes = Map.empty
            LastValidBoundingBoxes = Map.empty
            SelectedComponents = []
            SelectedWires = []
            NearbyComponents = []
            ErrorComponents = []
            DragToSelectBox = {TopLeft={X=0.0; Y=0.0}; H=0.0; W=0.0}
            ConnectPortsLine = {X=0.0; Y=0.0}, {X=0.0; Y=0.0}
            TargetPortId = ""
            Action = Idle
            LastMousePos = { X = 0.0; Y = 0.0 }
            SnapSymbols = emptySnap
            SnapSegments = emptySnap
            //ScrollPos = { X = 0.0; Y = 0.0 } Fix for scroll bug on changing sheets
            LastValidPos = { X = 0.0; Y = 0.0 }
            CurrentKeyPresses = Set.empty
            UndoList = []
            RedoList = []
            TmpModel = None
            Zoom = 1.0
            AutomaticScrolling = false
            ScrollingLastMousePos = {Pos={ X = 0.0; Y = 0.0 };Move={X=0.0; Y=0.0}}
            MouseCounter = 0
            LastMousePosForSnap = { X = 0.0; Y = 0.0 }
        }, Cmd.none

    | UpdateSelectedWires (connIds, on) ->
        let oldWires = model.SelectedWires
        let newWires =
            if on then oldWires @ connIds
            else List.filter (fun conId -> List.contains conId connIds |> not) oldWires
        {model with SelectedWires = newWires}, wireCmd (BusWireT.SelectWires newWires)
    | ColourSelection (compIds, connIds, colour) ->
        {model with SelectedComponents = compIds; SelectedWires = connIds},
        Cmd.batch [
            symbolCmd (SymbolT.ColorSymbols (compIds, colour)) // Better to have Symbol keep track of clipboard as symbols can get deleted before pasting.
            wireCmd (BusWireT.ColorWires (connIds, colour))
        ]
    | ResetSelection ->
        {model with SelectedComponents = []; SelectedWires = []},
        Cmd.batch [
            symbolCmd (SymbolT.SelectSymbols []) // Better to have Symbol keep track of clipboard as symbols can get deleted before pasting.
            wireCmd (BusWireT.SelectWires [])
        ]
    | SelectWires cIds ->
        //If any of the cIds of the netgroup given are inside the previous selection (not current as this will always be true)
        //then deselect (remove from the selected list) any wires from the selected list that are in that cId list
        //otherwise add the cId list to the selectedwires model
        let newWires =
            if List.exists (fun cId -> List.contains cId model.PrevWireSelection) cIds then
                List.filter (fun cId -> List.contains cId cIds |> not) model.PrevWireSelection
            else
                List.append cIds model.PrevWireSelection
        {model with SelectedWires = newWires}, 
        Cmd.batch [
            Cmd.ofMsg (ColourSelection([], newWires, HighLightColor.SkyBlue)); 
            wireCmd (BusWireT.SelectWires newWires)]
    | UpdateNodes -> 
        let comps = SymbolUpdate.extractComponents model.Wire.Symbol
        let conns = BusWire.extractConnections model.Wire
        let comps',conns' = combineGrounds (comps,conns)
        let nodeLst = createNodetoCompsList (comps',conns')
        let nodeLoc =
            [1..List.length nodeLst-1]
            |> List.map (fun i -> findConnectionsOnNode nodeLst i (conns'))
            |> List.map (findNodeLocation)

        {model with NodeLocations = nodeLoc}, Cmd.none
    | SetSpinner isOn ->
        if isOn then {model with CursorType = Spinner}, Cmd.none
        else {model with CursorType = Default}, Cmd.none
    | CanvasChanged ->
        {model with UpdateSim = true}, Cmd.none
    
    | ToggleNet _ | DoNothing | _ -> model, Cmd.none
    |> Optic.map fst_ postUpdateChecks


/// Init function
let init () =
    let wireModel, cmds = (BusWireUpdate.init ())
    let boundingBoxes = Symbol.getBoundingBoxes wireModel.Symbol

    {
        Wire = wireModel
        PopupViewFunc = None
        PopupDialogData = {Text=None; Int=None; Int2=None}
        BoundingBoxes = boundingBoxes
        LastValidBoundingBoxes = boundingBoxes
        SelectedComponents = []
        SelectedLabel = None
        SelectedWires = []
        NearbyComponents = []
        ErrorComponents = []
        DragToSelectBox = {TopLeft = {X=0.0; Y=0.0}; H=0.0; W=0.0}
        ConnectPortsLine = {X=0.0; Y=0.0}, {X=0.0; Y=0.0}
        TargetPortId = ""
        Action = Idle
        ShowGrid = false
        LastMousePos = { X = 0.0; Y = 0.0 }
        SnapSymbols=emptySnap
        SnapSegments = emptySnap
        CursorType = Default
        ScreenScrollPos = { X = 0.0; Y = 0.0 }
        LastValidPos = { X = 0.0; Y = 0.0 }
        CurrentKeyPresses = Set.empty
        UndoList = []
        RedoList = []
        TmpModel = None
        Zoom = 1.0
        CanvasSize = Constants.defaultCanvasSize
        AutomaticScrolling = false
        ScrollingLastMousePos = {Pos={ X = 0.0; Y = 0.0 }; Move={X = 0.0; Y  =0.0}}
        MouseCounter = 0
        LastMousePosForSnap = { X = 0.0; Y = 0.0 }
        CtrlKeyDown = false
        ScrollUpdateIsOutstanding = false
        PrevWireSelection = []
        NodeLocations = []
        ComponentCurrents = Map.empty
        NodeVoltages = []
        UpdateSim = false
        CanRunSimulation = true
        SimulationRunning = false
        ShowNodesOrVoltages = Neither
        ShowCurrents = false
        ACSim = []
        DCSim = emptyDCResults
        TimeSim = emptyTimeResults
    }, Cmd.none



