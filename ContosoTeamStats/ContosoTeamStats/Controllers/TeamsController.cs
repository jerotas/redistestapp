using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web.Mvc;
using ContosoTeamStats.Models;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace ContosoTeamStats.Controllers
{
    public class TeamsController : Controller
    {
        private readonly TeamContext _db = new TeamContext();

        private static readonly Lazy<ConnectionMultiplexer> LazyConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            var cacheConnection = ConfigurationManager.AppSettings["CacheConnection"];
            return ConnectionMultiplexer.Connect(cacheConnection);
        });

        public static ConnectionMultiplexer Connection => LazyConnection.Value;

        #region Actions

        public ActionResult TestSet()
        {
            var cache = Connection.GetDatabase();

            cache.StringSet("teamsList", "stuff");

            return View();
        }

        public ActionResult TestGet() {
            var cache = Connection.GetDatabase();

            var list = cache.StringGet("teamsList");
            ViewBag["list"] = list;

            return View();
        }

        // GET: Teams
        public ActionResult Index(string actionType, string resultType)
        {
            List<Team> teams;

            switch (actionType)
            {
                case "playGames": // Play a new season of games
                    PlayGames();
                    break;
                case "clearCache":
                    ClearCachedTeams();
                    break;
                case "rebuildDB":
                    RebuildDB();
                    break;
            }

            // Measure the time it takes to retrieve the results.
            var sw = Stopwatch.StartNew();

            switch (resultType) {
                case "teamsSortedSet": // Retrieve teams from sorted set.
                    teams = GetFromSortedSet();
                    break;

                case "teamsSortedSetTop5": // Retrieve the top 5 teams from the sorted set.
                    teams = GetFromSortedSetTop5();
                    break;

                case "teamsList": // Retrieve teams from the cached List<Team>.
                    teams = GetFromList();
                    break;

                // ReSharper disable once RedundantCaseLabel
                case "fromDB": // Retrieve results from the database.
                default:
                    teams = GetFromDB();
                    break;
            }

            sw.Stop();
            var ms = sw.ElapsedTicks / (Stopwatch.Frequency / (1000.0));

            // Add the elapsed time of the operation to the ViewBag.msg.
            ViewBag.msg += " MS: " + ms.ToString(CultureInfo.InvariantCulture);

            return View(teams);

        }

        // GET: Teams/Details/5
        public ActionResult Details(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            var team = _db.Teams.Find(id);
            if (team == null)
            {
                return HttpNotFound();
            }
            return View(team);
        }

        // GET: Teams/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: Teams/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create([Bind(Include = "ID,Name,Wins,Losses,Ties")] Team team)
        {
            if (ModelState.IsValid)
            {
                _db.Teams.Add(team);
                _db.SaveChanges();
                // When a team is added, the cache is out of date.
                // Clear the cached teams.
                ClearCachedTeams();
                return RedirectToAction("Index");
            }

            return View(team);
        }

        // GET: Teams/Edit/5
        public ActionResult Edit(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            var team = _db.Teams.Find(id);
            if (team == null)
            {
                return HttpNotFound();
            }
            return View(team);
        }

        // POST: Teams/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit([Bind(Include = "ID,Name,Wins,Losses,Ties")] Team team)
        {
            if (ModelState.IsValid)
            {
                _db.Entry(team).State = EntityState.Modified;
                _db.SaveChanges();
                // When a team is edited, the cache is out of date.
                // Clear the cached teams.
                ClearCachedTeams();
                return RedirectToAction("Index");
            }
            return View(team);
        }

        // GET: Teams/Delete/5
        public ActionResult Delete(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            var team = _db.Teams.Find(id);
            if (team == null)
            {
                return HttpNotFound();
            }
            return View(team);
        }

        // POST: Teams/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteConfirmed(int id)
        {
            var team = _db.Teams.Find(id);
            _db.Teams.Remove(team);
            _db.SaveChanges();
            // When a team is deleted, the cache is out of date.
            // Clear the cached teams.
            ClearCachedTeams();
            return RedirectToAction("Index");
        }
        #endregion

        void PlayGames() {
            ViewBag.msg += "Updating team statistics. ";
            // Play a "season" of games.
            var teams = from t in _db.Teams
                        select t;

            Team.PlayGames(teams);

            _db.SaveChanges();

            // Clear any cached results
            ClearCachedTeams();
        }

        void RebuildDB() {
            ViewBag.msg += "Rebuilding DB. ";
            // Delete and re-initialize the database with sample data.
            _db.Database.Delete();
            _db.Database.Initialize(true);

            // Clear any cached results
            ClearCachedTeams();
        }

        void ClearCachedTeams() {
            var cache = Connection.GetDatabase();
            cache.KeyDelete("teamsList");
            cache.KeyDelete("teamsSortedSet");
            ViewBag.msg += "Team data removed from cache. ";
        }

        List<Team> GetFromDB() {
            ViewBag.msg += "Results read from DB. ";
            var results = from t in _db.Teams
                          orderby t.Wins descending
                          select t;

            return results.ToList();
        }

        List<Team> GetFromList() {
            List<Team> teams;

            var cache = Connection.GetDatabase();
            string serializedTeams = cache.StringGet("teamsList");
            if (!string.IsNullOrEmpty(serializedTeams)) {
                teams = JsonConvert.DeserializeObject<List<Team>>(serializedTeams);

                ViewBag.msg += "List read from cache. ";
            } else {
                ViewBag.msg += "Teams list cache miss. ";
                // Get from database and store in cache
                teams = GetFromDB();

                ViewBag.msg += "Storing results to cache. ";
                cache.StringSet("teamsList", JsonConvert.SerializeObject(teams));
            }
            return teams;
        }

        List<Team> GetFromSortedSet() {
            List<Team> teams;
            var cache = Connection.GetDatabase();
            // If the key teamsSortedSet is not present, this method returns a 0 length collection.
            var teamsSortedSet = cache.SortedSetRangeByRankWithScores("teamsSortedSet", order: Order.Descending);
            if (teamsSortedSet.Length > 0) {
                ViewBag.msg += "Reading sorted set from cache. ";
                teams = new List<Team>();
                foreach (var t in teamsSortedSet) {
                    var tt = JsonConvert.DeserializeObject<Team>(t.Element);
                    teams.Add(tt);
                }
            } else {
                ViewBag.msg += "Teams sorted set cache miss. ";

                // Read from DB
                teams = GetFromDB();

                ViewBag.msg += "Storing results to cache. ";
                foreach (var t in teams) {
                    Console.WriteLine("Adding to sorted set: {0} - {1}", t.Name, t.Wins);
                    cache.SortedSetAdd("teamsSortedSet", JsonConvert.SerializeObject(t), t.Wins);
                }
            }
            return teams;
        }

        List<Team> GetFromSortedSetTop5() {
            var cache = Connection.GetDatabase();

            // If the key teamsSortedSet is not present, this method returns a 0 length collection.
            var teamsSortedSet = cache.SortedSetRangeByRankWithScores("teamsSortedSet", stop: 4, order: Order.Descending);
            if (teamsSortedSet.Length == 0) {
                // Load the entire sorted set into the cache.
                GetFromSortedSet();

                // Retrieve the top 5 teams.
                teamsSortedSet = cache.SortedSetRangeByRankWithScores("teamsSortedSet", stop: 4, order: Order.Descending);
            }

            ViewBag.msg += "Retrieving top 5 teams from cache. ";
            // Get the top 5 teams from the sorted set
            var teams = new List<Team>();
            foreach (var team in teamsSortedSet) {
                teams.Add(JsonConvert.DeserializeObject<Team>(team.Element));
            }
            return teams;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _db.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
