﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading; 
using NLog;
using Newtonsoft.Json;


namespace DMS
{
    public partial class RestoreDatabase : UserControl
    {
        private static readonly Lazy<RestoreDatabase> lazy =
            new Lazy<RestoreDatabase>(() => new RestoreDatabase(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static RestoreDatabase Instance { get { return lazy.Value; } }
        
        Config config;
        ObjectStorage objectStorage;
        private static Logger nlog = LogManager.GetCurrentClassLogger();
        Dictionary<Tuple<string, string, string>, Response> apiResponses = new Dictionary<Tuple<string, string, string>, Response>();
        Dictionary<Tuple<string, string, string>, Response> apiResponseTemp = new Dictionary<Tuple<string, string, string>, Response>();
        public event EventHandler<StatusEventArgs> StatusChangeEvent;
        private bool workBackupStorage = false; 
        private RestoreDatabase()
        {
            InitializeComponent();
            dgvBackupStorage.RowHeadersVisible = false;
            dgvBackupStorage.BackgroundColor = Color.White;
            
            config = Config.Instance;
            objectStorage = ObjectStorage.Instance;
            WriteConfig2TextBox();
        }

        public void WriteConfig2TextBox()
        {
            textFilterString.Text = config.GetEnumValue(Category.Upload, Key.FilterString);
            textInternalFolder.Text = config.GetEnumValue(Category.Upload, Key.InternalFolder);
            textStopAtTime.Text = config.GetEnumValue(Category.Upload, Key.StopAtTime);
            textNewDatabaseName.Text = config.GetEnumValue(Category.Upload, Key.NewDatabaseName);
        }

        void Keyword_EnterKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (workBackupStorage == true)
                    return; 

                ConfigSaveAllItem();
                BackupStorageFileList();
            }
        }
        
        private void buttonSave_Click(object sender, EventArgs e)
        {
            if (workBackupStorage == true)
                return; 
            ConfigSaveAllItem();
            BackupStorageFileList();
        }

        private void ConfigSaveAllItem()
        {
            nlog.Warn("ConfigSaveAllItem Started");
            config.SetEnumValue(Category.Upload, Key.FilterString, textFilterString.Text);
            config.SetEnumValue(Category.Upload, Key.InternalFolder, textInternalFolder.Text);
            config.SetEnumValue(Category.Upload, Key.StopAtTime, textStopAtTime.Text);
            config.SetEnumValue(Category.Upload, Key.NewDatabaseName, textNewDatabaseName.Text);
            lock (config.lockObject)
                config.WriteConfigToFile();
            nlog.Warn("ConfigSaveAllItem Completed");
        }

        private void StatusUpdate(Status status)
        {
            StatusChangeEvent?.Invoke(this, new StatusEventArgs(status));
        }

        private bool CheckBoxCountCheck (int cnt)
        {
            int checkboxCnt = 0;
            foreach (DataGridViewRow item in dgvBackupStorage.Rows)
            {
                if (bool.Parse(item.Cells[0].Value.ToString()))
                {
                    checkboxCnt++;
                }
            }
            if (checkboxCnt == 0 || checkboxCnt > cnt)
            {
                return false;
            }
            return true;             
        }

        private async void buttonRestoreDatabase_Click(object sender, EventArgs e)
        {
            nlog.Warn("buttonDatabaseRecovery Started");
            StatusUpdate(Status.Working);
            if (!CheckBoxCountCheck(1))
            {
                MessageBox.Show("please selecet one file");
                return;
            }
            
            ConfigSaveAllItem();
            string json = string.Empty;
            string checkStatus = string.Empty;
            checkStatus = checkRecovery.CheckState == CheckState.Checked ? "true" : "false";

            try

            {
                foreach (DataGridViewRow item in dgvBackupStorage.Rows)
                {
                    if (bool.Parse(item.Cells[0].Value.ToString()))
                    {

                        var postParams = new List<KeyValuePair<string, string>>();
                        AsyncCall asyncCall = new AsyncCall();
                        string recoveryFilename = string.Empty;

                        if (textInternalFolder.Text.Trim().Length > 0)
                            recoveryFilename = textInternalFolder.Text.Trim() + @"/" + item.Cells[1].EditedFormattedValue.ToString();
                        else
                            recoveryFilename = item.Cells[1].EditedFormattedValue.ToString();

                        long recoveryFileSize = Convert.ToInt64(item.Cells[2].EditedFormattedValue.ToString()); 
                        if (await IsMatchFileSizeInObjectStorage(recoveryFilename, recoveryFileSize))
                        {
                            postParams.Add(new KeyValuePair<string, string>("cloudDBInstanceNo", config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo)));
                            postParams.Add(new KeyValuePair<string, string>("fileName", recoveryFilename));
                            postParams.Add(new KeyValuePair<string, string>("isRecovery", checkStatus));
                            postParams.Add(new KeyValuePair<string, string>("newDatabaseName", config.GetEnumValue(Category.Upload, Key.NewDatabaseName)));
                            postParams.Add(new KeyValuePair<string, string>("responseFormatType", "json"));
                            string endpointUrl = config.GetEnumValue(Category.Config, Key.UseSSLApiGateway) == "1" ? @"https://" : @"http://";
                            endpointUrl = endpointUrl + config.GetEnumValue(Category.Config, Key.ApiUrl);

                            Task<string> result = asyncCall.WebApiCall(
                                endpointUrl
                                , GetPostType.POST
                                , @"/clouddb/v2/restoreDmsDatabase"
                                , postParams);

                            json = await result;
                            nlog.Warn(json);

                            restoreDmsDatabase restoreDmsDatabase = JsonConvert.DeserializeObject<restoreDmsDatabase>(json);
                            ApiResponseRecord(
                                config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo),
                                config.GetEnumValue(Category.Upload, Key.NewDatabaseName),
                                item.Cells[1].EditedFormattedValue.ToString()
                                , new Response
                                {
                                    requestNo = restoreDmsDatabase.restoreDmsDatabaseResponse.requestNo,
                                    requestReturnCode = restoreDmsDatabase.restoreDmsDatabaseResponse.returnCode,
                                    returnCode = "",
                                    returnCodeName = ""
                                });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                nlog.Error(string.Format("{0}, {1}", ex.Message, ex.StackTrace));
            }

            BackupStorageFileList();
            nlog.Warn("buttonDatabaseRecovery Completed");
        }


        private async Task<bool> IsMatchFileSizeInObjectStorage(string filename, long filesize)
        {
            bool isMatch = true;
            StatusUpdate(Status.Working);
            
            try
            {
                Task<List<ObjectStorageFile>> result = objectStorage.list(config.GetEnumValue(Category.Config, Key.ObjectBucket));
                List<ObjectStorageFile> files = await result;
                long uiRefresh = 0;
                foreach (var file in files)
                {
                    if(file.Name == filename)
                    {
                        if (file.Length != filesize)
                        {
                            MessageBox.Show(string.Format("file size does not match! objectStorageFileSize : {0}, internalStorageFileSize : {1}", file.Length, filesize));
                            nlog.Warn(string.Format("file size does not match! objectStorageFileSize : {0}, internalStorageFileSize : {1}", file.Length, filesize));
                            isMatch = false;
                        }
                    }
                    if (uiRefresh % 100 == 0) StatusUpdate(Status.Working);
                    uiRefresh++;
                }
            }
            catch (Exception ex)
            {
                nlog.Error(string.Format("Message : {0}, StackTrace : {1}", ex.Message, ex.StackTrace));
            }
            StatusUpdate(Status.Completed);
            return isMatch;  
        }

        private void buttonReload_Click(object sender, EventArgs e)
        {
            if (workBackupStorage) return; 
            BackupStorageFileList();
        }
        
        private void ApiResponseRecord(string cloudDbInstanceNo, string newDatabaseName, string filename, Response apiResponse)
        {
            if (apiResponses.ContainsKey(new Tuple<string, string, string>(cloudDbInstanceNo, newDatabaseName, filename)))
                apiResponses[new Tuple<string, string, string>(cloudDbInstanceNo, newDatabaseName, filename)] = apiResponse;
            else
                apiResponses.Add(new Tuple<string, string, string>(cloudDbInstanceNo, newDatabaseName, filename), apiResponse);
        }

        public async void BackupStorageFileList()
        {
            nlog.Warn("BackupStorageFileList Started");
            workBackupStorage = true;
            
            StatusUpdate(Status.Working);
            try
            {
                ConfigSaveAllItem();
                updateRestoreStatus();
                
                Task<string> result = getObjectStorageBackupListAync(textInternalFolder.Text);
                var settings = new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    MissingMemberHandling = MissingMemberHandling.Ignore
                };
                string getObjectStorageBackupListResult = await result; 
                getObjectStorageBackupList getObjectStorageBackupList = JsonConvert.DeserializeObject<getObjectStorageBackupList>(getObjectStorageBackupListResult, settings);
                List<DmsFileList> files = getObjectStorageBackupList.getObjectStorageBackupListResponse.dmsFileList;
                long uiRefresh = 0;
                dgvBackupStorage.InvokeIfRequired(s =>
                {
                    s.Rows.Clear();
                    s.RowHeadersVisible = false;
                    s.BackgroundColor = Color.White;
                });
                foreach (var file in files)
                {
                    if ((System.Text.RegularExpressions.Regex.IsMatch(file.fileName, textFilterString.Text, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) || textFilterString.Text.Length == 0)
                    {
                        string RequestNo = string.Empty;
                        string RequestReturnCode = string.Empty;
                        string ReturnCode = string.Empty;
                        string ReturnCodeName = string.Empty;

                        if (apiResponses.ContainsKey(new Tuple<string, string, string>(config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo), config.GetEnumValue(Category.Upload, Key.NewDatabaseName), file.fileName)))
                        {
                            RequestNo = apiResponses[new Tuple<string, string, string>(config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo), config.GetEnumValue(Category.Upload, Key.NewDatabaseName), file.fileName)].requestNo;
                            RequestReturnCode = apiResponses[new Tuple<string, string, string>(config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo), config.GetEnumValue(Category.Upload, Key.NewDatabaseName), file.fileName)].requestReturnCode;
                            ReturnCode = apiResponses[new Tuple<string, string, string>(config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo), config.GetEnumValue(Category.Upload, Key.NewDatabaseName), file.fileName)].returnCode;
                            ReturnCodeName = apiResponses[new Tuple<string, string, string>(config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo), config.GetEnumValue(Category.Upload, Key.NewDatabaseName), file.fileName)].returnCodeName;
                        }
                        dgvBackupStorage.InvokeIfRequired(s =>
                        {
                            int n = s.Rows.Add();
                            s.Rows[n].Cells[0].Value = "false";
                            s.Rows[n].Cells[1].Value = file.fileName;
                            s.Rows[n].Cells[2].Value = file.fileLength;
                            s.Rows[n].Cells[3].Value = String.Format("{0:yyyy-MM-dd HH:mm:ss}", System.DateTime.Parse(file.lastWriteTime));
                            s.Rows[n].Cells[4].Value = RequestNo;
                            s.Rows[n].Cells[5].Value = RequestReturnCode;
                            s.Rows[n].Cells[6].Value = ReturnCode;
                            s.Rows[n].Cells[7].Value = ReturnCodeName;
                        });
                        if (uiRefresh % 100 == 0) StatusUpdate(Status.Working);
                        uiRefresh++;
                    }
                }
            }
            catch (Exception ex)
            {
                nlog.Error(string.Format("{0}, {1}", ex.Message, ex.StackTrace));
               
            }
            finally
            {
                workBackupStorage = false;
            }
            StatusUpdate(Status.Completed);
            nlog.Warn("BackupStorageFileList Completed");
        }

        private async Task<string> getObjectStorageBackupListAync(string objectFolder)
        {
            nlog.Warn("BackupStorageFileListAync Started");
            workBackupStorage = true; 
            ConfigSaveAllItem();
            string json = string.Empty;
            try
            {
                var postParams = new List<KeyValuePair<string, string>>();
                AsyncCall asyncCall = new AsyncCall();
                postParams.Add(new KeyValuePair<string, string>("cloudDBInstanceNo", config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo)));
                postParams.Add(new KeyValuePair<string, string>("folderName", objectFolder));
                postParams.Add(new KeyValuePair<string, string>("responseFormatType", "json"));
                string endpointUrl = config.GetEnumValue(Category.Config, Key.UseSSLApiGateway) == "1" ? @"https://" : @"http://";
                endpointUrl = endpointUrl + config.GetEnumValue(Category.Config, Key.ApiUrl);

                Task<string> result = asyncCall.WebApiCall(
                    endpointUrl
                    , GetPostType.POST
                    , @"/clouddb/v2/getObjectStorageBackupList"
                    , postParams);

                json = await result;
                nlog.Warn(json);
            }
            catch (Exception ex)
            {
                nlog.Error(string.Format("{0}, {1}", ex.Message, ex.StackTrace));
            }
            finally
            {
                workBackupStorage = false;
            }

            nlog.Warn("BackupStorageFileListAync Completed");
            return json;
        }

        private async void updateRestoreStatus()
        {
            ConfigSaveAllItem();
            apiResponseTemp.Clear();
            string json = string.Empty;
            foreach (var a in apiResponses)
            {
                try
                {
                    var postParams = new List<KeyValuePair<string, string>>();
                    AsyncCall asyncCall = new AsyncCall();
                    postParams.Add(new KeyValuePair<string, string>("requestNo", a.Value.requestNo));
                    postParams.Add(new KeyValuePair<string, string>("responseFormatType", "json"));
                    string endpointUrl = config.GetEnumValue(Category.Config, Key.UseSSLApiGateway) == "1" ? @"https://" : @"http://";
                    endpointUrl = endpointUrl + config.GetEnumValue(Category.Config, Key.ApiUrl);

                    Task<string> result = asyncCall.WebApiCall(
                        endpointUrl
                        , GetPostType.POST
                        , @"/clouddb/v2/getDmsOperation"
                        , postParams);

                    json = await result;
                    var settings = new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore
                    };
                    getDmsOperation getDmsOperation = JsonConvert.DeserializeObject<getDmsOperation>(json, settings);

                    string codeName = string.Empty;
                    try
                    {
                        codeName = getDmsOperation.getDmsOperationResponse.status.codeName; 
                    }
                    catch  { }
                    if (codeName == string.Empty)
                    {
                        codeName = "UNKNOWN ERROR!";
                    }
                    
                    apiResponseTemp.Add(a.Key, new Response
                    {
                        requestNo = a.Value.requestNo,
                        requestReturnCode = a.Value.requestReturnCode,
                        returnCode = getDmsOperation.getDmsOperationResponse.returnCode,
                        returnCodeName = codeName
                    });
                    nlog.Warn(json);
                }
                catch (Exception ex)
                {
                    nlog.Error(string.Format("{0}, {1}", ex.Message, ex.StackTrace));
                }
            }
            
            try
            {
                foreach (var temp in apiResponseTemp) { apiResponses[temp.Key] = temp.Value; }
                apiResponseTemp.Clear();
            }
            catch(Exception ex)
            {
                nlog.Error(string.Format("{0}, {1}", ex.Message, ex.StackTrace));
            }
            
        }

        private async void buttonRestoreLog_Click(object sender, EventArgs e)
        {
            nlog.Warn("buttonLogRecovery Started");
            StatusUpdate(Status.Working);
            try
            {
                if (!CheckBoxCountCheck(1))
                {
                    MessageBox.Show("please selecet one file");
                    return;
                }
                ConfigSaveAllItem();
                string json = string.Empty;
                string checkStatus = string.Empty;
                checkStatus = checkRecovery.CheckState == CheckState.Checked ? "true" : "false";
                foreach (DataGridViewRow item in dgvBackupStorage.Rows)
                {
                    if (bool.Parse(item.Cells[0].Value.ToString()))
                    {

                        var postParams = new List<KeyValuePair<string, string>>();
                        AsyncCall asyncCall = new AsyncCall();

                        string recoveryFilename = string.Empty;
                        if (config.GetEnumValue(Category.Upload, Key.InternalFolder).Trim().Length > 0)
                            recoveryFilename = config.GetEnumValue(Category.Upload, Key.InternalFolder).Trim() + @"/" + item.Cells[1].EditedFormattedValue.ToString();
                        else
                            recoveryFilename = item.Cells[1].EditedFormattedValue.ToString();

                        long recoveryFileSize = Convert.ToInt64(item.Cells[2].EditedFormattedValue.ToString());
                        if (await IsMatchFileSizeInObjectStorage(recoveryFilename, recoveryFileSize))
                        {
                            postParams.Add(new KeyValuePair<string, string>("cloudDBInstanceNo", config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo)));
                            postParams.Add(new KeyValuePair<string, string>("fileName", recoveryFilename));
                            postParams.Add(new KeyValuePair<string, string>("isRecovery", checkStatus));
                            postParams.Add(new KeyValuePair<string, string>("newDatabaseName", config.GetEnumValue(Category.Upload, Key.NewDatabaseName)));
                            postParams.Add(new KeyValuePair<string, string>("responseFormatType", "json"));
                            if (textStopAtTime.Text != "0000-00-00T00:00:00+0900")
                                postParams.Add(new KeyValuePair<string, string>("stopTime", textStopAtTime.Text));
                            string endpointUrl = config.GetEnumValue(Category.Config, Key.UseSSLApiGateway) == "1" ? @"https://" : @"http://";
                            endpointUrl = endpointUrl + config.GetEnumValue(Category.Config, Key.ApiUrl);

                            Task<string> result = asyncCall.WebApiCall(
                                endpointUrl
                                , GetPostType.POST
                                , @"/clouddb/v2/restoreDmsTransactionLog"
                                , postParams);

                            json = await result;
                            nlog.Warn(json);
                            restoreDmsTransactionLog restoreDmsTransactionLog = JsonConvert.DeserializeObject<restoreDmsTransactionLog>(json);
                            ApiResponseRecord(
                                config.GetEnumValue(Category.Config, Key.CloudDbInstanceNo),
                                config.GetEnumValue(Category.Upload, Key.NewDatabaseName),
                                item.Cells[1].EditedFormattedValue.ToString()
                                , new Response
                                {
                                    requestNo = restoreDmsTransactionLog.restoreDmsTransactionLogResponse.requestNo,
                                    requestReturnCode = restoreDmsTransactionLog.restoreDmsTransactionLogResponse.returnCode,
                                    returnCode = "",
                                    returnCodeName = ""
                                });
                        }
                    }
                }
                BackupStorageFileList();
            }
            catch(Exception ex)
            {
                nlog.Error(string.Format("{0},{1}", ex.Message, ex.StackTrace));
            }
            nlog.Warn("buttonLogRecovery Completed");
        }
    }
}
