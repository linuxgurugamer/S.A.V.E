using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;
//using System.Threading;
using UnityEngine;

namespace Nereid
{
    namespace SAVE
    {
        [KSPAddon(KSPAddon.Startup.MainMenu, true)]
        public class BackupManager : MonoBehaviour
        {
            private const int SECS_RESTORE_WAIT = 2;

            public void Start()
            {
                SAVE.manager = this;

                Log.Info("starting backup/restore threads");
                //backupThread.Start();
                //restoreThread.Start();
                //StartCoroutine(BackupWork());
                //StartCoroutine(RestoreWork());
                StartCoroutine(MonitorCallbackGameSave());
                DontDestroyOnLoad(this);
            }

            public void Stop()
            {
                Log.Info("stopping backup/restore threads");
                stopRequested = true;
            }

            bool backupWorkIsRunning = false;

            IEnumerator BackupWork()
            {
                Log.Info("backup thread running");
                backupWorkIsRunning = true;
                while (!stopRequested)
                {
                    BackupJob job = backupQueue.Dequeue();
                    if (job != null)
                    {
                        Log.Info("executing backup job " + job);
                        job.Backup();
                        allBackupsCompleted = (backupQueue.Size() == 0);
                    }
                    else
                        yield return new WaitForSeconds(1);
                }
                Log.Info("backup thread terminated");
                backupWorkIsRunning = false;
            }

            bool restoreWorkIsRunning = false;
            IEnumerator RestoreWork()
            {
                Log.Info("restore thread running");
                restoreWorkIsRunning = true;
                while (!stopRequested)
                {
                    RestoreJob job = restoreQueue.Dequeue();
                    if (job != null)
                    {
                        Log.Info("executing restore job " + job);
                        job.Restore();
                        // wait at least 2 seconds;
                        yield return new WaitForSeconds(SECS_RESTORE_WAIT);
                        restoreCompleted = (restoreQueue.Size() == 0);
                    }
                    else
                        yield return new WaitForSeconds(1);
                }
                Log.Info("restore thread terminated");
                restoreWorkIsRunning = false;
            }


            public static String SAVE_ROOT = KSPUtil.ApplicationRootPath + "saves";

            private const String SAVE_GAME_TRAINING = "training";
            private const String SAVE_GAME_SCENARIOS = "scenarios";
            private const String SAVE_GAME_DESTRUCTIBLES = "scenarios";

            private List<BackupSet> backupSets = new List<BackupSet>();

            // array of names for display in GUI
            private String[] games;

            // Thread for doing backups...
            //private Thread backupThread;
            // Thread for doing restores...
            //private Thread restoreThread;
            // backup job queue
            internal readonly BlockingQueue<BackupJob> backupQueue = new BlockingQueue<BackupJob>();
            // restore job queue
            internal readonly BlockingQueue<RestoreJob> restoreQueue = new BlockingQueue<RestoreJob>();
            //
            internal volatile bool stopRequested = false;

            internal volatile bool allBackupsCompleted = true;
            internal volatile bool restoreCompleted = true;
            private volatile String restoredGame;


            private bool BuildInSaveGame(String name)
            {
                return name.Equals(SAVE_GAME_TRAINING) || name.Equals(SAVE_GAME_SCENARIOS) || name.Equals(SAVE_GAME_SCENARIOS);
            }

            private void AddBackup(String name, String folder)
            {
                if (!BuildInSaveGame(name))
                {
                    BackupSet set = new BackupSet(name, folder);
                    backupSets.Add(set);
                    set.ScanBackups();
                }
                else
                {
                    Log.Detail("save game '" + name + "' is buildin and ignored");
                }
            }

            public void ScanSavegames()
            {
                Log.Info("scanning save games");
                try
                {
                    // scan save games
                    foreach (String folder in Directory.GetDirectories(SAVE_ROOT))
                    {
                        Log.Info("save game found: '" + folder + "'");
                        String name = Path.GetFileName(folder);

                        if (GetBackupSetForName(name) == null)
                        {
                            Log.Detail("adding backup set '" + name + "'");
                            AddBackup(name, folder);
                        }
                    }
                    // scan backups (if save game folder was deleted)
                    foreach (String folder in Directory.GetDirectories(SAVE.configuration.backupPath))
                    {
                        String name = Path.GetFileName(folder);
                        BackupSet set = GetBackupSetForName(name);
                        if (set == null)
                        {
                            Log.Detail("adding backup set '" + name + "' (save game was deleted)");
                            AddBackup(name, SAVE_ROOT + "/" + name);
                        }
                    }
                    SortBackupSets();
                    CreateBackupSetNameArray();
                }
                catch (System.Exception e)
                {
                    Log.Error("failed to scan for save games: " + e.Message);
                }
            }

            private void SortBackupSets()
            {
                backupSets.Sort(delegate (BackupSet left, BackupSet right)
                {
                    return left.name.CompareTo(right.name);
                });
            }

            public BackupSet GetBackupSetForName(String name)
            {
                foreach (BackupSet set in backupSets)
                {
                    if (set.name.Equals(name)) return set;
                }
                return null;
            }

            public void CallbackGameSave(ConfigNode node)
            {
                Log.Info("callback: game about to save");
                WaitUntilAllCompleted();
                Log.Info("all backups completed before save");
            }

            bool doCallbackGameSave = false;
            Game game;
            public void CallbackGameSaved(Game game)
            {
                Log.Info("callback: game saved");
                doCallbackGameSave = true;
                this.game = game;
            }

            IEnumerator MonitorCallbackGameSave()
            {
                Log.Info("MonitorCallbackGameSave");
                while (true)
                {
                    yield return new WaitForSeconds(1f);
                    if (doCallbackGameSave)
                    {
                        StartCoroutine(CallbackGameSavedThread(game));
                        doCallbackGameSave = false;
                    }
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU)
                        Stop();
                    else
                    {
                        stopRequested = false;
                        if (!backupWorkIsRunning)
                            StartCoroutine(BackupWork());
                        if (!restoreWorkIsRunning)
                            StartCoroutine(RestoreWork());

                    }
                }
            }

            IEnumerator CallbackGameSavedThread(Game game)
            {
                Log.Info("CallbackupGameSavedThread");
                String name = HighLogic.SaveFolder;
                BackupSet set = GetBackupSetForName(name);
                if (set == null)
                {
                    set = new BackupSet(name, SAVE_ROOT + "/" + name);
                    backupSets.Add(set);
                    SortBackupSets();
                    CreateBackupSetNameArray();
                }
                //
                if (SAVE.configuration.disabled)
                {
                    Log.Info("backup disabled");
                    yield break;
                }
                TimeSpan elapsed = DateTime.Now - set.time;
                // 
                if (elapsed.Seconds <= 0)
                {
                    Log.Info("backup already done");
                    yield break;
                }

                Configuration.BACKUP_INTERVAL interval = SAVE.configuration.backupInterval;
                BackupJob job = BackupJob.NO_JOB;
                //if (!SAVE.configuration.asynchronous /*&& ! forceAsynchronous*/ )
                {
                    // wait for asynchronous backups to complete
                    while (backupQueue.Size() > 0)
                        yield return new WaitForSeconds(0.1f);

                }
                switch (interval)
                {
                    case Configuration.BACKUP_INTERVAL.EACH_SAVE:
                        BackupGame(set);
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_IN_10_MINUTES:
                        if (elapsed.TotalMinutes >= 10)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_IN_30_MINUTES:
                        if (elapsed.TotalMinutes >= 30)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_PER_HOUR:
                        if (elapsed.TotalHours >= 1)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_IN_2_HOURS:
                        if (elapsed.TotalHours >= 2)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_IN_4_HOURS:
                        if (elapsed.TotalHours >= 4)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_PER_DAY:
                        if (elapsed.TotalDays >= 1)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ONCE_PER_WEEK:
                        if (elapsed.TotalDays >= 7)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.CUSTOM:
                        if (elapsed.Minutes >= SAVE.configuration.customBackupInterval)
                        {
                            BackupGame(set);
                        }
                        break;
                    case Configuration.BACKUP_INTERVAL.ON_QUIT:
                        Log.Detail("backups are done every quit");
                        break;
                    default:
                        Log.Error("invalid backup interval ignored; backup is done each save");
                        BackupGame(set);
                        break;
                }
            }

            private IEnumerator WaitUntilAllCompleted()
            {
                if (!allBackupsCompleted) Log.Detail("waiting for backups to complete...");
                while (!allBackupsCompleted)
                {
                    yield return new WaitForSeconds(0.1f);

                }
                if (!allBackupsCompleted) Log.Detail("all backups completed");
            }

            public int BackupAll()
            {
                Log.Info("creating backup of all save games");
                int cnt = 0;
                allBackupsCompleted = false;
                foreach (BackupSet set in backupSets)
                {
                    BackupGame(set, true);
                    cnt++;
                }
                return cnt;
            }

            public BackupJob BackupGame(BackupSet set, bool forceAsynchronous = false)
            {
                Log.Info("adding backup job for " + set.name + " (" + backupQueue.Size() + " backups in queue)");
                allBackupsCompleted = false;
                BackupJob job = new BackupJob(set);
#if false
                if (SAVE.configuration.asynchronous || forceAsynchronous)
                {
                    Log.Info("adding backup job for " + set.name + " (" + backupQueue.Size() + " backups in queue)");
                    backupQueue.Enqueue(job);
                }
                else
#endif
                {
                    Log.Info("synchronous backup to backup set '" + set.name);

                    // do backup
                    job.Backup();
                    // done
                    allBackupsCompleted = true;
                }
                return job;
            }

            public BackupJob BackupGame(String game)
            {
                BackupSet set = GetBackupSetForName(game);
                if (set == null)
                {
                    set = new BackupSet(game, SAVE_ROOT + "/" + game);
                    backupSets.Add(set);
                    SortBackupSets();
                    CreateBackupSetNameArray();
                }
                return BackupGame(set);
            }

            public void CloneGameFromBackup(String game, String into)
            {
                Log.Info("cloning game from backup of '" + game + "' into '" + into + "'");
                BackupSet set = GetBackupSetForName(game);
                if (set != null)
                {
                    String from = set.Latest();
                    String to = SAVE_ROOT + "/" + into;
                    Log.Info("cloning game from '" + from + "' into '" + into + "'");
                    if (FileOperations.DirectoryExists(from))
                    {
                        if (FileOperations.DirectoryExists(to))
                        {
                            Log.Error("cloning of game failed: target folder exists");
                            return;
                        }
                        FileOperations.CopyDirectory(from, to);
                        // uncompress all compressed files
                        if (!FileOperations.DecompressFolder(to))
                        {
                            Log.Error("failed to decompress files");
                        }
                        // remove backup.ok and backup restored files
                        String backup_ok_file = to + "/backup.ok";
                        String backup_restored_file = to + "/backup.restored";
                        try
                        {
                            if (FileOperations.FileExists(backup_ok_file)) FileOperations.DeleteFile(backup_ok_file);
                            if (FileOperations.FileExists(backup_restored_file)) FileOperations.DeleteFile(backup_restored_file);
                        }
                        catch
                        {
                            Log.Warning("could not delete " + backup_ok_file + " or " + backup_restored_file);
                        }
                        ScanSavegames();
                    }
                    else
                    {
                        Log.Error("cloning of game failed: no backup folder to clone");
                    }
                }
                else
                {
                    Log.Error("cloning of game failed: no backup set '" + game + "' found");
                }
            }

            public void CloneGame(String game, String into)
            {
                String from = SAVE_ROOT + "/" + game;
                String to = SAVE_ROOT + "/" + into;
                Log.Info("cloning game from '" + from + "' into '" + into + "'");
                if (FileOperations.DirectoryExists(from))
                {
                    if (FileOperations.DirectoryExists(to))
                    {
                        Log.Error("cloning of game failed: target folder exists");
                        return;
                    }
                    FileOperations.CopyDirectory(from, to);
                    ScanSavegames();
                }
                else
                {
                    Log.Error("cloning of game failed: no save game folder to clone");
                }
            }


            public void CloneBackup(String game, String into)
            {
                Log.Info("cloning backup of '" + game + "' into '" + into + "'");
                game = FileOperations.GetFileName(game);
                BackupSet set = GetBackupSetForName(game);
                if (set != null)
                {
                    String from = SAVE.configuration.backupPath + "/" + game;
                    String to = SAVE.configuration.backupPath + "/" + into;
                    Log.Info("cloning backup from '" + from + "' into '" + into + "'");
                    if (FileOperations.DirectoryExists(from))
                    {
                        if (FileOperations.DirectoryExists(to))
                        {
                            Log.Error("cloning of backup failed: target folder exists");
                            return;
                        }
                        FileOperations.CopyDirectory(from, to);
                        BackupSet clone = GetBackupSetForName(into);
                        if (clone != null)
                        {
                            clone.ScanBackups();
                        }
                    }
                    else
                    {
                        Log.Error("cloning of backup failed: no backup folder to clone");
                    }
                }
                else
                {
                    Log.Error("cloning of backup failed: no backup set '" + game + "' found");
                }
            }

            public bool RestoreGame(String game, String from)
            {
                BackupSet set = GetBackupSetForName(game);
                if (set != null)
                {
                    StartCoroutine(RestoreGameThread(game, from));
                    return true;
                }
                else
                    return false;
            }
            IEnumerator RestoreGameThread(String game, String from)
            {
                BackupSet set = GetBackupSetForName(game);
                if (set != null)
                {
                    restoreCompleted = false;
                    restoredGame = game;
                    RestoreJob job = new RestoreJob(set, from);
                    Log.Warning("restoring game " + game);
#if false
                    if (SAVE.configuration.asynchronous)
                    {
                        Log.Info("asynchronous restore from backup set '" + game + "' backup '" + from + "'");
                        restoreQueue.Enqueue(job);
                    }
                    else
#endif
                    {
                        Log.Info("synchronous restore from backup set '" + game + "' backup '" + from + "'");
                        // wait for asynchronous restores to complete
                        while (restoreQueue.Size() > 0)
                            yield return new WaitForSeconds(0.1f);
                        // do restore
                        job.Restore();
                        // done
                        restoreCompleted = true;
                    }
                    yield break;
                }
                else
                {
                    Log.Warning("no backup set '" + game + "' found");
                    yield break; ;
                }

            }



            public String GetRestoredGame()
            {
                if (restoredGame == null) return "none";
                return restoredGame;
            }


            public int Queuedbackups()
            {
                return backupQueue.Size();
            }

            private void CreateBackupSetNameArray()
            {
                games = new String[backupSets.Count];
                int i = 0;
                foreach (BackupSet set in backupSets)
                {
                    games[i] = set.name;
                    i++;
                }
            }

            public String[] GetBackupSetNameArray()
            {
                if (games == null) CreateBackupSetNameArray();
                return games;
            }

            public bool BackupsCompleted()
            {
                return backupQueue.Size() == 0 && allBackupsCompleted;
            }

            public bool RestoreCompleted()
            {
                return restoreCompleted;
            }

            public void DeleteBackup(BackupSet backupSet, String backup)
            {
                if (backupSet == null) return;
                if (backup == null || backup.Length == 0) return;
                backupSet.DeleteBackup(backup);
            }

            public void EraseBackupSet(BackupSet backupSet)
            {
                if (backupSet == null) return;
                backupSet.Delete();
                backupSets.Remove(backupSet);
            }

            public int NumberOfBackupSets()
            {
                return backupSets.Count;
            }


            public System.Collections.IEnumerator GetEnumerator()
            {
                return backupSets.GetEnumerator();
            }
#if false
            IEnumerator<BackupSet> IEnumerable<BackupSet>.GetEnumerator()
            {
                return backupSets.GetEnumerator();
            }
#endif
        }
    }
}
