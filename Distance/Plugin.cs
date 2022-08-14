﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using CheapLoc;

using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Logging;
using Dalamud.Plugin;

namespace Distance
{
	public class Plugin : IDalamudPlugin
	{
		//	Initialization
		public Plugin(
			DalamudPluginInterface pluginInterface,
			Framework framework,
			ClientState clientState,
			CommandManager commandManager,
			Dalamud.Game.ClientState.Conditions.Condition condition,
			PartyList partyList,
			TargetManager targetManager,
			ChatGui chatGui,
			GameGui gameGui,
			DataManager dataManager,
			SigScanner sigScanner,
			ObjectTable objectTable )
		{
			//	API Access
			mPluginInterface	= pluginInterface;
			mFramework			= framework;
			mClientState		= clientState;
			mCommandManager		= commandManager;
			mCondition			= condition;
			mTargetManager		= targetManager;
			mChatGui			= chatGui;
			mGameGui			= gameGui;
			mDataManager		= dataManager;

			//	Initialization
			TargetResolver.Init( sigScanner, targetManager, objectTable );

			//	Configuration
			mConfiguration = mPluginInterface.GetPluginConfig() as Configuration;
			if( mConfiguration == null )
			{
				mConfiguration = new Configuration();
				mConfiguration.DistanceWidgetConfigs.Add( new() );
			}
			mConfiguration.Initialize( mPluginInterface );

			//	Aggro distance data loading
			Task.Run( async () =>
			{
				//	We can have the aggro distances data that got shipped with the plugin, or one that got downloaded.  Load in both and see which has the higher version to decide which to actually use.
				string aggroDistancesFilePath_Assembly = Path.Join( mPluginInterface.AssemblyLocation.DirectoryName, "Resources\\AggroDistances.dat" );
				string aggroDistancesFilePath_Config = Path.Join( mPluginInterface.GetPluginConfigDirectory(), "AggroDistances.dat" );
				BNpcAggroInfoFile aggroFile_Assembly = new();
				BNpcAggroInfoFile aggroFile_Config = new();
				aggroFile_Assembly.ReadFromFile( aggroDistancesFilePath_Assembly );
				if( File.Exists( aggroDistancesFilePath_Config ) )
				{
					aggroFile_Config.ReadFromFile( aggroDistancesFilePath_Config );
				}

				//	Auto-updating (if desired)
				if( mConfiguration.AutoUpdateAggroData )
				{
					var downloadedFile = await BNpcAggroInfoDownloader.DownloadUpdatedAggroDataAsync( Path.Join( mPluginInterface.GetPluginConfigDirectory(), "AggroDistances.dat" ) );
					aggroFile_Config = downloadedFile ?? aggroFile_Config;
				}
				
				var fileToUse = aggroFile_Config.FileVersion > aggroFile_Assembly.FileVersion ? aggroFile_Config : aggroFile_Assembly;
				BNpcAggroInfo.Init( mDataManager, fileToUse );
			} );

			//	Localization and Command Initialization
			OnLanguageChanged( mPluginInterface.UiLanguage );

			//	UI Initialization
			mUI = new PluginUI( this, mPluginInterface, mConfiguration, mDataManager, mGameGui, mClientState, mCondition );
			mPluginInterface.UiBuilder.Draw += DrawUI;
			mPluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
			mUI.Initialize();
			NameplateHandler.Init( sigScanner, clientState, partyList, condition, gameGui, mConfiguration );

			//	We need to disable automatic hiding, because we actually turn off our game UI nodes in the draw functions as-appropriate, so we can't skip the draw functions.
			mPluginInterface.UiBuilder.DisableAutomaticUiHide = true;

			//	Event Subscription
			mPluginInterface.LanguageChanged += OnLanguageChanged;
			mFramework.Update += OnGameFrameworkUpdate;
			mClientState.TerritoryChanged += OnTerritoryChanged;
		}

		//	Cleanup
		public void Dispose()
		{
			mFramework.Update -= OnGameFrameworkUpdate;
			mClientState.TerritoryChanged -= OnTerritoryChanged;
			mPluginInterface.UiBuilder.Draw -= DrawUI;
			mPluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
			mPluginInterface.LanguageChanged -= OnLanguageChanged;
			mCommandManager.RemoveHandler( mTextCommandName );
			mUI.Dispose();

			BNpcAggroInfoDownloader.CancelAllDownloads();

			NameplateHandler.Uninit();
			TargetResolver.Uninit();
		}

		protected void OnLanguageChanged( string langCode )
		{
			//***** TODO *****
			var allowedLang = new List<string>{ /*"de", "ja", "fr", "it", "es"*/ };

			PluginLog.Information( "Trying to set up Loc for culture {0}", langCode );

			if( allowedLang.Contains( langCode ) )
			{
				Loc.Setup( File.ReadAllText( Path.Join( Path.Join( mPluginInterface.AssemblyLocation.DirectoryName, "Resources\\Localization\\" ), $"loc_{langCode}.json" ) ) );
			}
			else
			{
				Loc.SetupWithFallbacks();
			}

			//	Set up the command handler with the current language.
			if( mCommandManager.Commands.ContainsKey( mTextCommandName ) )
			{
				mCommandManager.RemoveHandler( mTextCommandName );
			}
			mCommandManager.AddHandler( mTextCommandName, new CommandInfo( ProcessTextCommand )
			{
				HelpMessage = String.Format( Loc.Localize( "Plugin Text Command Description", "Use {0} for a listing of available text commands." ), "\"/pdistance help\"" )
			} );
		}

		//	Text Commands
		protected void ProcessTextCommand( string command, string args )
		{
			//*****TODO: Don't split, just substring off of the first space so that other stuff is preserved verbatim.
			//	Seperate into sub-command and paramters.
			string subCommand = "";
			string subCommandArgs = "";
			string[] argsArray = args.Split( ' ' );
			if( argsArray.Length > 0 )
			{
				subCommand = argsArray[0];
			}
			if( argsArray.Length > 1 )
			{
				//	Recombine because there might be spaces in JSON or something, that would make splitting it bad.
				for( int i = 1; i < argsArray.Length; ++i )
				{
					subCommandArgs += argsArray[i] + ' ';
				}
				subCommandArgs = subCommandArgs.Trim();
			}

			//	Process the commands.
			bool suppressResponse = mConfiguration.SuppressCommandLineResponses;
			string commandResponse = "";
			if( subCommand.Length == 0 )
			{
				//	For now just have no subcommands act like the config subcommand
				mUI.SettingsWindowVisible = !mUI.SettingsWindowVisible;
			}
			else if( subCommand.ToLower() == "config" )
			{
				mUI.SettingsWindowVisible = !mUI.SettingsWindowVisible;
			}
			else if( subCommand.ToLower() == "enable" )
			{
				commandResponse = ProcessTextCommand_Enable( subCommandArgs );
			}
			else if( subCommand.ToLower() == "disable" )
			{
				commandResponse = ProcessTextCommand_Disable( subCommandArgs );
			}
			else if( subCommand.ToLower() == "toggle" )
			{
				commandResponse = ProcessTextCommand_Toggle( subCommandArgs );
			}
			else if( subCommand.ToLower() == "debug" )
			{
				mUI.DebugWindowVisible = !mUI.DebugWindowVisible;
			}
			else if( subCommand.ToLower() == "help" || subCommand.ToLower() == "?" )
			{
				commandResponse = ProcessTextCommand_Help( subCommandArgs );
				suppressResponse = false;
			}
			else
			{
				commandResponse = ProcessTextCommand_Help( subCommandArgs );
			}

			//	Send any feedback to the user.
			if( commandResponse.Length > 0 && !suppressResponse )
			{
				mChatGui.Print( commandResponse );
			}
		}

		protected string ProcessTextCommand_Enable( string args )
		{
			if( args.Trim().Length == 0 ) return Loc.Localize( "Text Command Response: No Widget Name Provided", "No widget name was specified!" );

			var configs = mConfiguration.DistanceWidgetConfigs.FindAll( x => { return x.WidgetName == args.Trim(); });
			foreach( var config in configs )
			{
				config.Enabled = true;
			}

			string retString = "";

			if( configs.Count == 0 )
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Enable - None Found", "No widget(s) named \"{0}\" could be found." ), args.Trim() );
			}
			else if( configs.Count == 1 )
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Enable - One Found", "The widget named \"{0}\" was enabled." ), args.Trim() );
			}
			else
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Enable - Multiple Found", "All {0} widgets named \"{1}\" were enabled." ), configs.Count, args.Trim() );
			}

			return retString;
		}

		protected string ProcessTextCommand_Disable( string args )
		{
			if( args.Trim().Length == 0 ) return Loc.Localize( "Text Command Response: No Widget Name Provided", "No widget name was specified!" );

			var configs = mConfiguration.DistanceWidgetConfigs.FindAll( x => { return x.WidgetName == args.Trim(); });
			foreach( var config in configs )
			{
				config.Enabled = false;
			}

			string retString = "";

			if( configs.Count == 0 )
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Disable - None Found", "No widget(s) named \"{0}\" could be found." ), args.Trim() );
			}
			else if( configs.Count == 1 )
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Disable - One Found", "The widget named \"{0}\" was disabled." ), args.Trim() );
			}
			else
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Disable - Multiple Found", "All {0} widgets named \"{1}\" were disabled." ), configs.Count, args.Trim() );
			}

			return retString;
		}

		protected string ProcessTextCommand_Toggle( string args )
		{
			if( args.Trim().Length == 0 ) return Loc.Localize( "Text Command Response: No Widget Name Provided", "No widget name was specified!" );

			var configs = mConfiguration.DistanceWidgetConfigs.FindAll( x => { return x.WidgetName == args.Trim(); });
			foreach( var config in configs )
			{
				config.Enabled = !config.Enabled;
			}

			string retString = "";

			if( configs.Count == 0 )
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Toggle - None Found", "No widget(s) named \"{0}\" could be found." ), args.Trim() );
			}
			else if( configs.Count == 1 )
			{
				if( configs[0].Enabled )
				{
					retString = String.Format( Loc.Localize( "Text Command Response: Toggle - One Found (enabled)", "The widget named \"{0}\" was enabled." ), args.Trim() );
				}
				else
				{
					retString = String.Format( Loc.Localize( "Text Command Response: Toggle - One Found (disabled)", "The widget named \"{0}\" was disabled." ), args.Trim() );
				}
			}
			else
			{
				retString = String.Format( Loc.Localize( "Text Command Response: Toggle - Multiple Found", "All {0} widgets named \"{1}\" were toggled." ), configs.Count, args.Trim() );
			}

			return retString;
		}

		protected string ProcessTextCommand_Help( string args )
		{
			if( args.ToLower() == "config" )
			{
				return Loc.Localize( "Help Message: Config Subcommand", "Opens the settings window." );
			}
			if( args.ToLower() == "enable" )
			{
				return String.Format( Loc.Localize( "Help Message: Enable Subcommand", "Enables the specified distance widget.  Usage: \"{0} <widget name>\"" ), "/pdistance enable" );
			}
			if( args.ToLower() == "disable" )
			{
				return String.Format( Loc.Localize( "Help Message: Disable Subcommand", "Disables the specified distance widget.  Usage: \"{0} <widget name>\"" ), "/pdistance disable" );
			}
			if( args.ToLower() == "toggle" )
			{
				return String.Format( Loc.Localize( "Help Message: Toggle Subcommand", "Toggles the specified distance widget on or off.  Usage: \"{0} <widget name>\"" ), "/pdistance toggle" );
			}
			else
			{
				return String.Format( Loc.Localize( "Help Message: Basic", "Valid subcommands are {0}, {1}, {2}, and {3}.  Use \"{4} <subcommand>\" for more information on each subcommand." ), "\"config\"", "\"enable\"", "\"disable\"", "\"toggle\"", "/pdistance help" );
			}
		}

		public void OnGameFrameworkUpdate( Framework framework )
		{
			UpdateTargetDistanceData();

			if( mConfiguration.NameplateDistancesConfig.ShowNameplateDistances ) NameplateHandler.EnableNameplateDistances();
			else NameplateHandler.DisableNameplateDistances();
		}

		protected void OnTerritoryChanged( object sender, UInt16 ID )
		{
			//	Pre-filter when we enter a zone so that we have a lower chance of stutters once we're actually in.
			BNpcAggroInfo.FilterAggroEntities( ID );
		}

		public DistanceInfo GetDistanceInfo( TargetType targetType )
		{
			return mCurrentDistanceInfoArray[(int)targetType];
		}

		public bool ShouldDrawAggroDistanceInfo()
		{
			//if( mClientState.IsPvP ) return false;

			//***** TODO: We probably need some director info to make it not show as curtain is coming up.  Condition and addon visibility are are failing us here.
			return	mConfiguration.ShowAggroDistance &&
					GetDistanceInfo( mConfiguration.AggroDistanceApplicableTargetType ).IsValid &&
					GetDistanceInfo( mConfiguration.AggroDistanceApplicableTargetType ).TargetKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
					GetDistanceInfo( mConfiguration.AggroDistanceApplicableTargetType ).HasAggroRangeData &&
					(TargetResolver.GetTarget( mConfiguration.AggroDistanceApplicableTargetType ) as BattleChara )?.CurrentHp > 0 &&
					!mCondition[ConditionFlag.Unconscious] &&
					!mCondition[ConditionFlag.InCombat];
		}

		//	It's tempting to put this into the config filters class, but we rely on a few things that won't know about, so just keeping it here to avoid having to pass in even more stuff.
		public bool ShouldDrawDistanceInfo( DistanceWidgetConfig config )
		{
			// if( mClientState.IsPvP ) return false;
			if( !config.Enabled ) return false;
			if( config.HideInCombat && mCondition[ConditionFlag.InCombat] ) return false;
			if( config.HideOutOfCombat && !mCondition[ConditionFlag.InCombat] ) return false;
			if( !mCurrentDistanceInfoArray[(int)config.ApplicableTargetType].IsValid ) return false;

			bool show = mCurrentDistanceInfoArray[(int)config.ApplicableTargetType].TargetKind switch
			{
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc			=> config.Filters.ShowDistanceOnBattleNpc,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player			=> config.Filters.ShowDistanceOnPlayers && mCurrentDistanceInfoArray[(int)config.ApplicableTargetType].ObjectID != mClientState.LocalPlayer?.ObjectId,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc			=> config.Filters.ShowDistanceOnEventNpc,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure			=> config.Filters.ShowDistanceOnTreasure,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte			=> config.Filters.ShowDistanceOnAetheryte,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint	=> config.Filters.ShowDistanceOnGatheringNode,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj			=> config.Filters.ShowDistanceOnEventObj,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion			=> config.Filters.ShowDistanceOnCompanion,
				Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Housing			=> config.Filters.ShowDistanceOnHousing,
				_ => false,
			};
			return show;
		}

		protected void UpdateTargetDistanceData()
		{
			if( mClientState.LocalPlayer == null)
			{
				foreach( var info in mCurrentDistanceInfoArray )
				{
					info.Invalidate();
				}

				return;
			}

			for( int i = 0; i < mCurrentDistanceInfoArray.Length; ++i )
			{
				var target = TargetResolver.GetTarget( (TargetType)i );
				if( target != null )
				{
					mCurrentDistanceInfoArray[i].IsValid = true;
					mCurrentDistanceInfoArray[i].TargetKind = target.ObjectKind;
					mCurrentDistanceInfoArray[i].ObjectID = target.ObjectId;
					mCurrentDistanceInfoArray[i].PlayerPosition = mClientState.LocalPlayer.Position;
					mCurrentDistanceInfoArray[i].TargetPosition = target.Position;
					mCurrentDistanceInfoArray[i].TargetRadius_Yalms = target.HitboxRadius;
					mCurrentDistanceInfoArray[i].BNpcID = ( target as Dalamud.Game.ClientState.Objects.Types.BattleNpc )?.NameId ?? 0;
					float? aggroRange = BNpcAggroInfo.GetAggroRange( mCurrentDistanceInfoArray[i].BNpcID, mClientState.TerritoryType );
					mCurrentDistanceInfoArray[i].HasAggroRangeData = aggroRange.HasValue;
					mCurrentDistanceInfoArray[i].AggroRange_Yalms = aggroRange ?? 0;
				}
				else
				{
					mCurrentDistanceInfoArray[i].Invalidate();
				}
			}
		}

		protected void DrawUI()
		{
			mUI.Draw();
		}

		protected void DrawConfigUI()
		{
			mUI.SettingsWindowVisible = true;
		}

		public string Name => "Distance";
		protected const string mTextCommandName = "/pdistance";

		protected readonly DistanceInfo[] mCurrentDistanceInfoArray = new DistanceInfo[Enum.GetNames(typeof(TargetType)).Length];
		protected DalamudPluginInterface mPluginInterface;
		protected Framework mFramework;
		protected ClientState mClientState;
		protected CommandManager mCommandManager;
		protected Dalamud.Game.ClientState.Conditions.Condition mCondition;
		protected TargetManager mTargetManager;
		protected ChatGui mChatGui;
		protected GameGui mGameGui;
		protected DataManager mDataManager;
		protected Configuration mConfiguration;
		protected PluginUI mUI;
	}
}
