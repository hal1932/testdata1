#r "System.dll"
#r "System.Xml.Linq.dll"
#r "MySQL.Data.dll"
using System;
using System.IO;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MySql.Data.MySqlClient;

var cResourceRoot = Path.GetFullPath("resources");
var cShopCount = 100;
var cShopTypeCount = 5;
var cItemCount = 3000;
var cItemTypeCount = 10;
var cModelCountPerItem = new KeyValuePair<int, int>(0, 5);
var cItemCoutnPerShop = new KeyValuePair<int, int>(1, 20);
var cItemPrice = new KeyValuePair<int, int>(1, 1000);
var cItemPriceFactor = 10;

var cMaxThreadCount = 8;

var rand = new Random(1234567890);
var lockObj = new object();


class Model
{
    public FileInfo File { get; private set; }
    public FileInfo Def { get; private set; }
    public FileInfo[] Textures { get; private set; } = new FileInfo[] { };

    public Model(string filepath)
    {
        File = new FileInfo(filepath);
        if (!File.Exists)
        {
            throw new InvalidOperationException("cannot find model file: " + filepath);
        }

        Def = File.Directory.EnumerateFiles("*.mdldef")
            .FirstOrDefault();
        if (Def == null || !Def.Exists)
        {
            throw new InvalidOperationException("cannot find modeldef file: " + filepath);
        }

        var xml = XElement.Load(Def.FullName);
        Textures = xml.Element("Textures")
            .Elements()
            .Select(xelem => new FileInfo(Path.Combine(Def.Directory.FullName, xelem.Element("Path").Value)))
            .ToArray();
    }
}

try
{
    Main(Environment.GetCommandLineArgs());
}
catch (Exception e)
{
    Console.WriteLine(e);
    Console.WriteLine(e.Message);
}

void Main(string[] args)
{
    ThreadPool.SetMinThreads(1, 1);
    ThreadPool.SetMaxThreads(8, 8);

    // テーブル初期化
    using (var conn = OpenConnection())
    {
        NonQuery(conn, "start transaction");

		NonQuery(conn, "set foreign_key_checks=0");
		ClearTable(conn, "models");
		ClearTable(conn, "textures");
		ClearTable(conn, "files");
		ClearTable(conn, "model_texture_maps");
		NonQuery(conn, "set foreign_key_checks=1");

        NonQuery(conn, "commit");
    }
	
	// ファイル登録
	{
		var filelists = Enumerable.Range(0, cMaxThreadCount)
			.Select(_ => new List<string>())
			.ToArray();
		var index = 0;
		foreach (var type in new string[] { "*.mdl", "*.tex" })
		{
			foreach (var file in Directory.EnumerateFiles(cResourceRoot, type, SearchOption.AllDirectories))
			{
				filelists[index % cMaxThreadCount].Add(file);
				++index;
			}
		}
		
		Parallel.ForEach(filelists, files =>
		{
			using (var conn = OpenConnection())
			{
				NonQuery(conn, "start transaction");
				
				foreach (var file in files)
				{
					var path = CalcRelativePath(file, cResourceRoot);
					
					var info = new FileInfo(file);
					var createdAt = CalcDateTimeStr(info.CreationTime);
					var lastUpdatedAt = CalcDateTimeStr(info.LastWriteTime);
					
					var query =
						$" insert into `files`" +
						$"   (`path`, `created_at`, `last_updated_at`)" +
						$"  values ('{path}', '{createdAt}', '{lastUpdatedAt}')";
					NonQuery(conn, query);
				}
				
				NonQuery(conn, "commit");
			}
		});
	}

    // モデル、テクスチャ登録
	{
		var modellists = Enumerable.Range(0, cMaxThreadCount)
			.Select(_ => new List<Model>())
			.ToArray();
		var index = 0;
		foreach (var type in new string[] { "*.mdl", "*.tex" })
		{
			foreach (var file in Directory.EnumerateFiles(Path.Combine(cResourceRoot, "models"), "*.mdl", SearchOption.AllDirectories))
			{
				modellists[index % cMaxThreadCount].Add(new Model(file));
				++index;
			}
		}
		
		Dictionary<string, int> files;
		using (var conn = OpenConnection())
		{
			files = Query(conn, "select `id`, `path` from `files`")
				.ToDictionary(item => item["path"] as string, item => (int)item["id"]);
		}
	
		var modelIds = new List<ulong>();
		var texIdDic = new Dictionary<string, ulong>();
		Parallel.ForEach(modellists, models =>
		{
			using (var conn = OpenConnection())
			{
				NonQuery(conn, "start transaction");

				// モデル
				foreach (var model in models)
				{
					ulong modelId = 0;
					{
						var name = Path.GetFileNameWithoutExtension(model.File.Name);
						var user = "user" + rand.Next(50).ToString("D2");
						
						var filepath = CalcRelativePath(model.File.FullName, cResourceRoot);
						var fileId = files[filepath];

						var query =
							$"insert into `models`" +
							$"  (`name`, `operator`, `file_id`)" +
							$"  values ('{name}', '{user}', '{fileId}')";
						NonQuery(conn, query);

						modelId = (ulong)QueryScalar(conn, "select last_insert_id()");

						lock(lockObj)
						{
							modelIds.Add(modelId);
						}
					}

					// テクスチャ
					foreach (var tex in model.Textures)
					{
						var name = Path.GetFileNameWithoutExtension(tex.Name);
						var user = "user" + rand.Next(50).ToString("D2");
						var width = 256 * rand.Next(1, 9);
						var height = 256 * rand.Next(1, 9);
						var bitDepth = (int)Math.Pow(2, rand.Next(4));
						
						var filepath = CalcRelativePath(tex.FullName, cResourceRoot);
						var fileId = files[filepath];

						var query = 
							$"insert ignore into `textures`" +
							$"  (`name`, `width`, `height`, `bit_depth`, `operator`, `file_id`)" +
							$"  values ('{name}', {width}, {height}, {bitDepth}, '{user}', {fileId})";

						ulong texId = 0;
						lock (lockObj)
						{
							if (NonQuery(conn, query) == 0)
							{
								texId = texIdDic[name];
							}
							else
							{
								texId = (ulong)QueryScalar(conn, "select last_insert_id()");
								texIdDic[name] = texId;
							}
						}

						query =
							$"insert into `model_texture_maps`" +
							$"  (`model_id`, `texture_id`)" +
							$"  values ({modelId}, {texId})";
						NonQuery(conn, query);
					}
				}
				
				NonQuery(conn, "commit");
			}
		});
	}
}


//********************************************************//


MySqlConnection OpenConnection()
{
    lock (lockObj)
    {
        var conn = new MySqlConnection("server=localhost;uid=yuta;pwd=Rubstuns19;database=test");
        conn.Open();
        return conn;
    }
}

void ClearTable(MySqlConnection conn, string name)
{
	NonQuery(conn, $"truncate table `{name}`");
	NonQuery(conn, $"alter table `{name}` auto_increment=1");
}

IEnumerable<Dictionary<string, object>> Query(MySqlConnection conn, string query)
{
    var adapter = new MySqlDataAdapter(query, conn);
    var data = new DataTable();
    adapter.Fill(data);

    foreach (DataRow row in data.Rows)
    {
        var item = new Dictionary<string, object>();
        foreach (DataColumn col in data.Columns)
        {
            item[col.ColumnName] = row[col];
        }
		yield return item;
    }
}

object QueryScalar(MySqlConnection conn, string query)
{
    var command = new MySqlCommand(query, conn);
    command.CommandTimeout = 0;
    return command.ExecuteScalar();
}

int NonQuery(MySqlConnection conn, string query)
{
    var command = new MySqlCommand(query, conn);
    command.CommandTimeout = 0;
    return command.ExecuteNonQuery();
}

string CalcRelativePath(string path, string root)
{
    var relativeUrl = new Uri(root + Path.DirectorySeparatorChar)
        .MakeRelativeUri(new Uri(path));
    return relativeUrl.ToString().Replace(Path.DirectorySeparatorChar, '/');
}

string CalcDateTimeStr(DateTime value)
{
    return value.ToString("yyyy-MM-dd HH:mm:ss");
}
