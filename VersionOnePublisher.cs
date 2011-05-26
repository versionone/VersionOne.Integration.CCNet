using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Exortech.NetReflector;
using ThoughtWorks.CruiseControl.Core;
using ThoughtWorks.CruiseControl.Remote;
using VersionOne.SDK.ObjectModel;
using VersionOne.SDK.ObjectModel.Filters;

/**
 * VersionOne would like to thank Andreas Axelsson for his 
 * contribution to improving this component.
 */
namespace ccnet.VersionOnePublisher.plugin {
    [ReflectorType("versionone")]
    public class VersionOnePublisher : ITask {
        private string url;
        [ReflectorProperty("url")]
        public string Url {
            get {
                return url.EndsWith("/") ? url : url + "//";
            } 
            set { url = value; }
        }

        private string username;
        [ReflectorProperty("username", Required = false)]
        public string Username { get { return username; } set { username = value; } }

        private string password;
        [ReflectorProperty("password", Required = false)]
        public string Password { get { return password; } set { password = value; } }

        private bool integrated;
        [ReflectorProperty("integratedauth", Required = false)]
        public bool Integrated { get { return integrated; } set { integrated = value; } }

        private bool useProxy;
        [ReflectorProperty("useproxy", Required = false)]
        public bool UseProxy { get { return useProxy; } set { useProxy = value; } }

        private string proxyUrl;
        [ReflectorProperty("proxyurl", Required = false)]
        public string ProxyUrl { get { return proxyUrl; } set { proxyUrl = value; } }

        private string proxyUsername;
        [ReflectorProperty("proxyusername", Required = false)]
        public string ProxyUsername { get { return proxyUsername; } set { proxyUsername = value; } }

        private string proxyPassword;
        [ReflectorProperty("proxypassword", Required = false)]
        public string ProxyPassword { get { return proxyPassword; } set { proxyPassword = value; } }

        private string proxyDomain;
        [ReflectorProperty("proxydomain", Required = false)]
        public string ProxyDomain { get { return proxyDomain; } set { proxyDomain = value; } }

        private string ccWebRoot;
        [ReflectorProperty("webroot", Required = false)]
        public string CcWebRoot { get { return ccWebRoot; } set { ccWebRoot = value; } }

        private string referenceExpression;
        /// <summary>
        /// Pattern used to match the VersionOne field on Workitems to check-in comments.
        /// </summary>
        [ReflectorProperty("referenceexpression", Required = false)]
        public string ReferenceExpression { get { return referenceExpression; } set { referenceExpression = value; } }

        private string referenceField;
        /// <summary>
        /// The name of the VersionOne field used to associate check-in comments with Workitems.
        /// </summary>
        [ReflectorProperty("referencefield", Required = false)]
        public string ReferenceField { get { return referenceField; } set { referenceField = value; } }

        private string ccBuildServer;
        [ReflectorProperty("server", Required = false)]
        public string CcBuildServer { get { return ccBuildServer; } set { ccBuildServer = value; } }

        private V1Instance instance;
        private V1Instance Instance {
            get { return instance ?? (instance = CreateV1Instance()); }
        }

        private V1Instance CreateV1Instance() {
            ProxySettings proxy = GetProxy();
            V1Instance newV1 = new V1Instance(Url, Username, Password, Integrated, proxy);
            newV1.Validate();

            return newV1;
        }

        private ProxySettings GetProxy() {
            if(!UseProxy) {
                return null;
            }
            Uri uri = new Uri(ProxyUrl);
            return new ProxySettings(uri, ProxyUsername, ProxyPassword, ProxyDomain);
        }

        public void Run(IIntegrationResult result) {
            BuildProject buildProject = GetBuildProject(result);
            if(buildProject != null) {
                IEnumerable<ChangeInfo> changes = ResolveChanges(result.Modifications);
                BuildRun run = buildProject.CreateBuildRun(result.ProjectName + " - " + result.Label, GetBuildDate(result));
                run.Elapsed = GetElapsed(result);
                run.Reference = result.Label;
                run.Source.CurrentValue = DetermineSource(result);
                run.Status.CurrentValue = DetermineStatus(result);
                run.Description = GetModificationDescription(changes);
                run.Save();
                string str = CreateBuildUrl(result);
                if (!string.IsNullOrEmpty(str)) {
                    run.CreateLink("Build Report", str, true);
                }
                SetChangeSets(run, changes);
            }
        }

        /// <summary>
        /// Cull out a distinct set of changes from the CCNet Modifications collection.
        /// </summary>
        /// <param name="modifications">The CCNet Modifications collection.</param>
        /// <returns>A collection of ChangeInfo.</returns>
        private static IEnumerable<ChangeInfo> ResolveChanges(IEnumerable<Modification> modifications) {
            IDictionary<string, ChangeInfo> changes = new Dictionary<string, ChangeInfo>();

            foreach (Modification mod in modifications) {
                if (!changes.ContainsKey(mod.ChangeNumber)) {
                    changes.Add(mod.ChangeNumber, new ChangeInfo(mod));
                }
            }
            return changes.Values;
        }

        private class ChangeInfo {
            public readonly string Number;
            public readonly string Comment;
            public readonly string User;
            public readonly DateTime Stamp;

            public ChangeInfo(Modification mod) {
                Number = mod.ChangeNumber;
                Comment = mod.Comment;
                User = mod.UserName;
                Stamp = mod.ModifiedTime;
            }
        }

        /// <summary>
        /// Associate this BuildRun with one or more ChangeSets. If there is no ChangeSet, then create one.
        /// </summary>
        /// <param name="run">This BuildRun.</param>
        /// <param name="changes">The changes as reported by CCNet.</param>
        private void SetChangeSets(BuildRun run, IEnumerable<ChangeInfo> changes) {
            foreach(ChangeInfo change in changes) {
                // See if we have this ChangeSet in the system.
                ChangeSetFilter filter = new ChangeSetFilter();
                filter.Reference.Add(change.Number.ToString());
                ICollection<ChangeSet> changeSets = Instance.Get.ChangeSets(filter);
                if(changeSets.Count == 0) {
                    // We don't have one yet. Create one.
                    string name = string.Format("{0} on {1}", change.User, change.Stamp);
                    ChangeSet changeSet = Instance.Create.ChangeSet(name, change.Number.ToString());
                    changeSet.Description = change.Comment;
                    changeSets = new List<ChangeSet>();
                    changeSets.Add(changeSet);
                    Trace("Created new ChangeSet: {0}", name);
                }

                IEnumerable<PrimaryWorkitem> workitems = DetermineWorkitems(change);

                // Associate with our new BuildRun.
                foreach(ChangeSet changeSet in changeSets) {
                    run.ChangeSets.Add(changeSet);
                    foreach(PrimaryWorkitem workitem in workitems) {
                        changeSet.PrimaryWorkitems.Add(workitem);
                        //workitem.CompletedIn.Clear();

                        List<BuildRun> toRemove = new List<BuildRun>();
                        foreach (BuildRun otherRun in workitem.CompletedIn) {
                            if (otherRun.BuildProject == run.BuildProject) {
                                toRemove.Add(otherRun);
                            }
                        }

                        foreach (BuildRun buildRun in toRemove) {
                            workitem.CompletedIn.Remove(buildRun);
                        }

                        workitem.CompletedIn.Add(run);
                        Trace("Associated ChangeSet with PrimaryWorkitem: {0}", workitem.ID);
                    }
                }
            }
        }

        /// <summary>
        /// Pull the workitem numbers out of the change comments.
        /// </summary>
        /// <param name="change">The CCNet change record.</param>
        /// <returns>A collection of affected PrimaryWorkitems.</returns>
        private IEnumerable<PrimaryWorkitem> DetermineWorkitems(ChangeInfo change) {
            List<PrimaryWorkitem> result = new List<PrimaryWorkitem>();
            if(!string.IsNullOrEmpty(ReferenceExpression) && !string.IsNullOrEmpty(ReferenceField)) {
                Regex expression = new Regex(ReferenceExpression);
                foreach(Match match in expression.Matches(change.Comment))
                    result.AddRange(ResolveReference(match.Value));
            } else {
                Trace("Either referenceexpression ({0}) or referencefield ({1}) not set in config file.", ReferenceExpression, ReferenceField);
            }
            return result;
        }

        /// <summary>
        /// Resolve a check-in comment identifier to a PrimaryWorkitem.
        /// </summary>
        /// <param name="reference">The identifier in the check-in comment.</param>
        /// <returns>A collection of matching PrimaryWorkitems.</returns>
        /// <remarks>If the reference matches a SecondaryWorkitem, we need to navigate to the parent.</remarks>
        private IEnumerable<PrimaryWorkitem> ResolveReference(string reference) {
            List<PrimaryWorkitem> result = new List<PrimaryWorkitem>();

            WorkitemFilter filter = new WorkitemFilter();
            filter.Find.SearchString = reference;
            filter.Find.Fields.Add(ReferenceField);
            IEnumerable<Workitem> workitems = Instance.Get.Workitems(filter);
            foreach(Workitem workitem in workitems) {
                if(workitem is PrimaryWorkitem) {
                    result.Add((PrimaryWorkitem)workitem);
                } else if(workitem is SecondaryWorkitem) {
                    result.Add(((SecondaryWorkitem)workitem).Parent);
                } else {
                    // Shut 'er down, Clancy, she's pumping mud.
                    throw new ApplicationException(string.Format("Found unexpected Workitem type: {0}", workitem.GetType()));
                }
            }

            Trace("Associated {0} PrimaryWorkitem(s) with {1}.", result.Count, reference);

            return result;
        }

        private static string GetModificationDescription(IEnumerable<ChangeInfo> changes) {
            StringBuilder result = new StringBuilder();
            foreach (ChangeInfo change in changes) {
                result.Append(string.Format("{0}: {1}<br>", change.User, change.Comment));
            }
            return result.ToString();
        }

        private static DateTime GetBuildDate(IIntegrationResult result) {
            return ((result.EndTime == DateTime.MinValue) ? DateTime.Now : result.EndTime);
        }

        private static double GetElapsed(IIntegrationResult result) {
            DateTime time = (result.EndTime == DateTime.MinValue) ? DateTime.Now : result.EndTime;
            TimeSpan span = time - result.StartTime;
            return span.TotalMilliseconds;
        }

        private static string DetermineStatus(IIntegrationResult result) {
            switch(result.Status) {
                case IntegrationStatus.Success:
                    return "Passed";
                case IntegrationStatus.Failure:
                    return "Failed";
                default:
                    return null;
            }
        }

        private static string DetermineSource(IIntegrationResult result) {
            switch(result.BuildCondition) {
                case BuildCondition.ForceBuild:
                    return "Forced";
                case BuildCondition.IfModificationExists:
                    return "Trigger";
                default:
                    return null;
            }
        }

        public string CreateBuildUrl(IIntegrationResult result) {
            if(CcWebRoot == null) {
                return null;
            }
            List<string> parts = new List<string>();

            string buildTime = (string)result.IntegrationProperties["CCNetBuildTime"];
            string buildDate = (string)result.IntegrationProperties["CCNetBuildDate"];
            string buildProject = (string)result.IntegrationProperties["CCNetProject"];
            string buildLabel = (string)result.IntegrationProperties["CCNetLabel"];

            buildTime = buildTime.Replace(":", string.Empty);
            buildDate = buildDate.Replace("-", string.Empty);

            string file = "log" + buildDate + buildTime + (result.Succeeded ? "Lbuild." + buildLabel : string.Empty) + ".xml";

            parts.Add(CcWebRoot);

            if (!CcWebRoot.EndsWith("/")) {
                parts.Add("/");
            }            
            parts.Add("server/");
            parts.Add(String.IsNullOrEmpty(CcBuildServer) ? "local" : CcBuildServer);
            parts.Add("/project/");
            parts.Add(buildProject);
            parts.Add("/build/");
            parts.Add(file);
            parts.Add("/ViewBuildReport.aspx");

            return string.Join(string.Empty, parts.ToArray());
        }

        /// <summary>
        /// Find the first BuildProject where the Reference matches the result.ProjectName.
        /// </summary>
        /// <param name="result">The CC integration result.</param>
        /// <returns>The BuildProject if we have a match; otherwise, null.</returns>
        private BuildProject GetBuildProject(IIntegrationResult result) {
            BuildProjectFilter filter = new BuildProjectFilter();
            filter.References.Add(result.ProjectName);
            filter.State.Add(State.Active);
            ICollection<BuildProject> projects = Instance.Get.BuildProjects(filter);

            foreach (BuildProject project in projects) {
                return project;
            }
            Trace("Couldn't find BuildProject for {0}", result.ProjectName);
            return null;
        }

        private static void Trace(string format, params object[] args) {
            System.Diagnostics.Trace.WriteLine(string.Format(format, args));
        }
    }
}