using Godot;
using System;
using System.Collections.Generic;
using redhatgamedev.srt;

public class Server : Node
{
  Random rnd = new Random();

  CSLogger cslogger;

  AMQPserver MessageInterface;

  [Export]
  Dictionary<String, Node2D> playerObjects = new Dictionary<string, Node2D>();

  // the "width" of a hex is 2 * size
  [Export]
  public Int32 SectorSize = 1600;

  public Layout HexLayout;

  // starting ring radius is zero - just one sector
  [Export]
  public int RingRadius = 0;

  // the sector map will only store the number of players in each sector
  // it only gets updated when a new player joins
  [Export]
  Dictionary<String, int> sectorMap = new Dictionary<string, int>();

  [Export]
  public Int32 StarFieldRadiusPixels;

  // The starfield's center may shift during play at small ring sizes due to the
  // way that sectors are added
  [Export]
  Vector2 StarFieldCenter = new Vector2(0,0);
  
  [Export]
  float CameraMinZoom = 4f;

  [Export]
  float CameraMaxZoom = 0.1f;

  [Export]
  float CameraZoomStepSize = 0.1f;

  Vector2 CameraCurrentZoom = new Vector2(1,1);

  Queue<SecurityCommandBuffer> PlayerJoinQueue = new Queue<SecurityCommandBuffer>();

  /* PLAYER DEFAULTS AND CONFIG */

  float PlayerDefaultThrust = 1f;
  float PlayerDefaultMaxSpeed = 5;
  float PlayerDefaultRotationThrust = 1.5f;
  int PlayerDefaultHitPoints = 100;
  int PlayerDefaultMissileSpeed = 300;
  float PlayerDefaultMissileLife = 4;
  int PlayerDefaultMissileDamage = 25;

  /* END PLAYER DEFAULTS AND CONFIG */

  void SendGameUpdates()
  {
    cslogger.Verbose("Server.cs: Sending updates about game state to clients");

    foreach(KeyValuePair<String, Node2D> entry in playerObjects)
    {
      cslogger.Verbose($"Server.cs: Sending update for player: {entry.Key}");

      // find the PlayerShip
      PlayerShip thePlayer = entry.Value.GetNode<PlayerShip>("PlayerShip");

      // create the buffer for the specific player and send it
      EntityGameEventBuffer egeb = thePlayer.CreatePlayerGameEventBuffer(EntityGameEventBuffer.EntityGameEventBufferType.Update);

      // send the player create event message
      MessageInterface.SendGameEvent(egeb);
    }

    // TODO: need to send missile updates
    // TODO: missiles need to be in a group
  }

  public void RemovePlayer(String UUID)
  {
    cslogger.Debug($"Server.cs: Removing player: {UUID}");
    Node2D thePlayerToRemove = playerObjects[UUID];
    PlayerShip thePlayer = thePlayerToRemove.GetNode<PlayerShip>("PlayerShip");

    // TODO: should this get wrapped with a try or something?
    thePlayerToRemove.QueueFree();
    playerObjects.Remove(UUID);

    // create the buffer for the specific player and send it
    EntityGameEventBuffer egeb = thePlayer.CreatePlayerGameEventBuffer(EntityGameEventBuffer.EntityGameEventBufferType.Destroy);

    // send the player create event message
    MessageInterface.SendGameEvent(egeb);
    
  }

  Hex TraverseSectors()
  {
    Hex theCenter = new Hex(0,0,0);

    // based around the function from
    // https://www.redblobgames.com/grids/hexagons/#rings

    // need to iterate over all the rings
    for (int x = 1; x <= RingRadius; x++)
    {
      // pick the 0th sector in a ring
      Hex theSector = theCenter.Add( Hex.directions[4].Scale(x) );

      // traverse the ring
      for (int i = 0; i < 6; i++)
      {
        for (int j = 0; j < RingRadius; j++)
        {
          string theKey = $"{theSector.q},{theSector.r}";
          if (sectorMap.ContainsKey(theKey))
          { 
            // the sector map has the sector we're looking at, so verify how many
            // players are in it. if there are less than two players, use it.
            if (sectorMap[theKey] < 2) { return theSector; } 
          }
          else
          {
            // the sector map doesn't have the key for the sector we're looking
            // at. this means the sector definitely has zero players in it.
            return theSector;
          }

          // if we get here, it means that the current sector is full, so move
          // to the next neighbor
          theSector = theSector.Neighbor(i);
        }
      }
    }

    // we got to the end of the rings without finding a sector, so make a new
    // ring and return that ring's first sector

    RingRadius++;
    return theCenter.Add( Hex.directions[4].Scale(RingRadius) );
  }

  void UpdateSectorMap()
  {
    // re-initilize the sector map
    sectorMap.Clear();

    foreach(KeyValuePair<String, Node2D> entry in playerObjects)
    {
      PlayerShip thePlayer = entry.Value.GetNode<PlayerShip>("PlayerShip");
      FractionalHex theHex = HexLayout.PixelToHex(new Point(thePlayer.GlobalPosition.x, thePlayer.GlobalPosition.y));
      Hex theRoundedHex = theHex.HexRound();

      // the key can be axial coordinates
      String theSectorKey = $"{theRoundedHex.q},{theRoundedHex.r}";

      // check if the key exists in the dict
      if (sectorMap.ContainsKey(theSectorKey))
      {
        // increment it if it does
        sectorMap[theSectorKey] += 1;
      }
      else
      { 
        // initialize it to 1 if it doesn't
        sectorMap[theSectorKey] = 1;
      }
    }
  }
  void InstantiatePlayer(String UUID)
  {
    // Update the sector map in preparation for traversing the rings, expanding
    // the radius, and etc.  need to do this before adding the new player object
    // because we don't know where that player will go until we traverse the
    // existing sectors, and because the physics process will kick in as soon
    // as the node is created
    UpdateSectorMap();

    // start with the center
    Hex theSector = new Hex(0,0,0);

    PackedScene playerShipThing = (PackedScene)ResourceLoader.Load("res://Player.tscn");
    Node2D playerShipThingInstance = (Node2D)playerShipThing.Instance();

    PlayerShip newPlayer = playerShipThingInstance.GetNode<PlayerShip>("PlayerShip");
    newPlayer.uuid = UUID;

    // assign the configured values
    newPlayer.Thrust = PlayerDefaultThrust;
    newPlayer.MaxSpeed = PlayerDefaultMaxSpeed;
    newPlayer.RotationThrust = PlayerDefaultRotationThrust;
    newPlayer.HitPoints = PlayerDefaultHitPoints;
    newPlayer.MissileSpeed = PlayerDefaultMissileSpeed;
    newPlayer.MissileLife = PlayerDefaultMissileLife;
    newPlayer.MissileDamage = PlayerDefaultMissileDamage;

    playerObjects.Add(UUID, playerShipThingInstance);

    // if there are more than two players, it means we are now at the point
    // where we have to start calculating ring things
    if (playerObjects.Count > 2)
    {

      // if the ring radius is zero, and we have more than two players, we need
      // to increase it, otherwise things will already blow up
      if (RingRadius == 0) 
      { 
        RingRadius++;
      }

      // it's possible that we have insufficient players in sector 0,0,0, so
      // check that first for funzos
      if (sectorMap["0,0"] < 2)
      { 
        // do nothing since we already assigned the sector to use to 0,0,0
      }

      else
      {
        theSector = TraverseSectors();
      }
    }

    // reset the starfield radius - should also move the center
    StarFieldRadiusPixels = (RingRadius+1) * SectorSize * 2;

    // now that the sector to insert the player has been selected, find its
    // pixel center
    Point theSectorCenter = HexLayout.HexToPixel(theSector);

    // TODO: need to ensure players are not on top of one another for real.  we
    // will spawn two players into a sector to start, so we should check if
    // there's already a player in the sector first. if there is, we should
    // place the new player equidistant from the already present player

    // badly randomize start position
    int theMin = (int)(SectorSize * 0.3);
    int xOffset = rnd.Next(-1 * theMin, theMin);
    int yOffset = rnd.Next(-1 * theMin, theMin);

    playerShipThingInstance.GlobalPosition = new Vector2(x: (Int32)theSectorCenter.x + xOffset,
                                y: (Int32)theSectorCenter.y + yOffset);

    AddChild(playerShipThingInstance);
    cslogger.Debug("Server.cs: Added player instance!");

    // create the protobuf for the player joining
    EntityGameEventBuffer egeb = newPlayer.CreatePlayerGameEventBuffer(EntityGameEventBuffer.EntityGameEventBufferType.Create);

    // send the player create event message
    MessageInterface.SendGameEvent(egeb);
  }

  void ProcessMoveCommand(CommandBuffer cb)
  {
    cslogger.Verbose("Server.cs: Processing move command!");
    DualStickRawInputCommandBuffer dsricb = cb.rawInputCommandBuffer.dualStickRawInputCommandBuffer;

    String uuid = cb.rawInputCommandBuffer.Uuid;
    Node2D playerRoot = playerObjects[uuid];

    // find the PlayerShip
    PlayerShip movePlayer = playerRoot.GetNode<PlayerShip>("PlayerShip");

    // process thrust and rotation
    Vector2 thrust = new Vector2(dsricb.pbv2Move.X, dsricb.pbv2Move.Y);

    // push the thrust input onto the player's array
    movePlayer.MovementQueue.Enqueue(thrust);
  }

  void ProcessShootCommand(CommandBuffer cb)
  {
    cslogger.Debug("Server.cs: Processing shoot command!");
    DualStickRawInputCommandBuffer dsricb = cb.rawInputCommandBuffer.dualStickRawInputCommandBuffer;

    String uuid = cb.rawInputCommandBuffer.Uuid;
    Node2D playerRoot = playerObjects[uuid];

    // find the PlayerShip
    PlayerShip movePlayer = playerRoot.GetNode<PlayerShip>("PlayerShip");

    movePlayer.FireMissile();
  }

  void ProcessSecurityGameEvent(SecurityCommandBuffer securityCommandBuffer)
  {
    cslogger.Verbose("Server.cs: Processing security command buffer!");
    switch (securityCommandBuffer.Type)
    {
      case SecurityCommandBuffer.SecurityCommandBufferType.Join:
        cslogger.Info($"Server.cs: Join UUID: {securityCommandBuffer.Uuid}");
        // TODO: buffer this because sometimes it collides with sending game
        // updates and an exception is fired because the player collection is
        // modified during looping over it
        PlayerJoinQueue.Enqueue(securityCommandBuffer);
        break;
      case SecurityCommandBuffer.SecurityCommandBufferType.Leave:
        cslogger.Info($"Server.cs: Leave UUID: {securityCommandBuffer.Uuid}");
        break;
    }
  }

  void ProcessPlayerJoins()
  {

    while (PlayerJoinQueue.Count > 0)
    {
      SecurityCommandBuffer scb = PlayerJoinQueue.Dequeue();
      InstantiatePlayer(scb.Uuid);
    }

  }

  public void ProcessGameEvent(CommandBuffer CommandBuffer)
  {
    switch (CommandBuffer.Type)
    {
      case CommandBuffer.CommandBufferType.Security:
        cslogger.Verbose("Server.cs: Security event!");
        ProcessSecurityGameEvent(CommandBuffer.securityCommandBuffer);
        break;
      case CommandBuffer.CommandBufferType.Rawinput:
        cslogger.Verbose("Raw input event!");

        if (CommandBuffer.rawInputCommandBuffer.dualStickRawInputCommandBuffer.pbv2Move != null)
        { ProcessMoveCommand(CommandBuffer); }

        if (CommandBuffer.rawInputCommandBuffer.dualStickRawInputCommandBuffer.pbv2Shoot != null)
        { ProcessShootCommand(CommandBuffer); }
        break;
    }
  }

  public void LoadConfig()
  {
    var serverConfig = new ConfigFile();
    Error err = serverConfig.Load("server.cfg");

    // If the file didn't load, ignore it.
    if (err != Error.Ok) { return; }

    // server settings
    SectorSize = (Int32) serverConfig.GetValue("game","sector_size");

    // player settings
    // https://stackoverflow.com/questions/24447387/cast-object-containing-int-to-float-results-in-invalidcastexception
    PlayerDefaultThrust = Convert.ToSingle(serverConfig.GetValue("player","thrust"));
    PlayerDefaultMaxSpeed = Convert.ToSingle(serverConfig.GetValue("player","max_speed"));
    PlayerDefaultRotationThrust = Convert.ToSingle(serverConfig.GetValue("player","rotation_thrust"));
    PlayerDefaultHitPoints = (int) serverConfig.GetValue("player","hit_points");
    PlayerDefaultMissileSpeed = (int) serverConfig.GetValue("player","missile_speed");
    PlayerDefaultMissileLife = Convert.ToSingle(serverConfig.GetValue("player","missile_life"));
    PlayerDefaultMissileDamage = (int) serverConfig.GetValue("player","missile_damage");

  }

  // Called when the node enters the scene tree for the first time.
  public override void _Ready()
  {
    // initialize the logging configuration
    Node gdlogger = GetNode<Node>("/root/GDLogger");
    gdlogger.Call("load_config", "res://logger.cfg");
    cslogger = GetNode<CSLogger>("/root/CSLogger");

    cslogger.Info("Space Ring Things (SRT) Game Server");

    MessageInterface = GetNode<AMQPserver>("/root/AMQPserver");

    cslogger.Info("Beginning game server");

    LoadConfig();

    // TODO: output the current config

    // initialize the starfield size to the initial sector size
    // the play area is clamped 
    StarFieldRadiusPixels = SectorSize;

    // initialize the hexboard layout
    HexLayout = new Layout(Layout.flat, new Point(SectorSize,SectorSize), new Point(0,0));

    //  GetTree().Quit();
  }


  // ****** THINGS RELATED TO DEBUG ******
  void ProcessInputEvent(Vector2 velocity, Vector2 shoot)
  {
    // fetch the UUID from the text field to use in the message
    CanvasLayer theCanvas = GetNode<CanvasLayer>("DebugUI");
    LineEdit textField = theCanvas.GetNode<LineEdit>("PlayerID");

    // if there is no player in the dictionary, do nothing
    // this catches accidental keyboard hits
    if (!playerObjects.ContainsKey(textField.Text)) { return; }

    // there was some kind of input, so construct a message to send to the server
    CommandBuffer cb = new CommandBuffer();
    cb.Type = CommandBuffer.CommandBufferType.Rawinput;

    RawInputCommandBuffer ricb = new RawInputCommandBuffer();
    ricb.Type = RawInputCommandBuffer.RawInputCommandBufferType.Dualstick;
    ricb.Uuid = textField.Text;

    DualStickRawInputCommandBuffer dsricb = new DualStickRawInputCommandBuffer();
    if ( (velocity.Length() > 0) || (shoot.Length() > 0) )

    if (velocity.Length() > 0)
    {
      Box2d.PbVec2 b2dMove = new Box2d.PbVec2();
      b2dMove.X = velocity.x;
      b2dMove.Y = velocity.y;
      dsricb.pbv2Move = b2dMove;
    }

    if (shoot.Length() > 0)
    {
      // TODO: make this actually depend on ship direction
      Box2d.PbVec2 b2dShoot = new Box2d.PbVec2();
      b2dShoot.Y = 1;
      dsricb.pbv2Shoot = b2dShoot;
    }

    ricb.dualStickRawInputCommandBuffer = dsricb;
    cb.rawInputCommandBuffer = ricb;
    MessageInterface.SendCommand(cb);
  }

  // TODO: should move debug to its own scene that's optionally loaded
  void _on_JoinAPlayer_pressed()
  {
    CanvasLayer theCanvas = GetNode<CanvasLayer>("DebugUI");
    LineEdit textField = theCanvas.GetNode<LineEdit>("PlayerID");

    // don't do anything if this UUID already exists
    if (playerObjects.ContainsKey(textField.Text)) { return; }

    cslogger.Debug($"Server.cs: Sending join with UUID: {textField.Text}");

    // construct a join message from the text in the debug field
    SecurityCommandBuffer scb = new SecurityCommandBuffer();
    scb.Uuid = textField.Text;
    scb.Type = SecurityCommandBuffer.SecurityCommandBufferType.Join;

    CommandBuffer cb = new CommandBuffer();
    cb.Type = CommandBuffer.CommandBufferType.Security;
    cb.securityCommandBuffer = scb;
    MessageInterface.SendCommand(cb);
  }

  public override void _UnhandledInput(InputEvent @event)
  {

    // hop out if we don't have a player to zoom in on
    CanvasLayer theCanvas = GetNode<CanvasLayer>("DebugUI");
    LineEdit textField = theCanvas.GetNode<LineEdit>("PlayerID");
    if (!playerObjects.ContainsKey(textField.Text)) { return; }

    // grab the camera and zoom it by zoom factor
    Node2D playerForCamera = playerObjects[textField.Text];
    Camera2D playerCamera = playerForCamera.GetNode<Camera2D>("PlayerShip/Camera2D");

    if (@event.IsActionPressed("zoom_in"))
    { 
      cslogger.Debug("Server.cs: zoom viewport in!");
      float zoomN = CameraCurrentZoom.x - CameraZoomStepSize;
      zoomN = Mathf.Clamp(zoomN, CameraMaxZoom, CameraMinZoom);
      CameraCurrentZoom.x = zoomN;
      CameraCurrentZoom.y = zoomN;
      playerCamera.Zoom = CameraCurrentZoom;
      cslogger.Debug($"Server.cs: Zoom Level: {CameraCurrentZoom.x}, {CameraCurrentZoom.y}");
    }

    if (@event.IsActionPressed("zoom_out"))
    {
      cslogger.Debug("Server.cs zoom viewport out!");
      float zoomN = CameraCurrentZoom.x + CameraZoomStepSize;
      zoomN = Mathf.Clamp(zoomN, CameraMaxZoom, CameraMinZoom);
      CameraCurrentZoom.x = zoomN;
      CameraCurrentZoom.y = zoomN;
      playerCamera.Zoom = CameraCurrentZoom;
      cslogger.Debug($"Server.cs: Zoom Level: {CameraCurrentZoom.x}, {CameraCurrentZoom.y}");
    }
  }

  // Called every frame. 'delta' is the elapsed time since the previous frame.
  public override void _Process(float delta)
  {

    // loosely based on: https://godotengine.org/qa/116981/object-follow-mouse-in-radius
    // get the UUID of the text box and set that ship's camera to active
    CanvasLayer theCanvas = GetNode<CanvasLayer>("DebugUI");
    LineEdit textField = theCanvas.GetNode<LineEdit>("PlayerID");
    if (playerObjects.ContainsKey(textField.Text)) 
    { 
      Node2D playerForCamera = playerObjects[textField.Text];
      Camera2D playerCamera = playerForCamera.GetNode<Camera2D>("PlayerShip/Camera2D");
      if (!playerCamera.Current) { playerCamera.MakeCurrent(); }
    }

    // look for any inputs, subsequently sent a control message
    var velocity = Vector2.Zero; // The player's movement direction.
    var shoot = Vector2.Zero; // the player's shoot status

    if (Input.IsActionPressed("rotate_right"))
    {
      velocity.x += 1;
    }

    if (Input.IsActionPressed("rotate_left"))
    {
      velocity.x -= 1;
    }

    if (Input.IsActionPressed("thrust_forward"))
    {
      velocity.y += 1;
    }

    if (Input.IsActionPressed("thrust_reverse"))
    {
      velocity.y -= 1;
    }

    if (Input.IsActionPressed("fire"))
    {
      shoot.y = 1;
    }

    if ( (velocity.Length() > 0) || (shoot.Length() > 0) )
    {
      ProcessInputEvent(velocity, shoot);
    }

    ProcessPlayerJoins();

    SendGameUpdates();
  }
}
