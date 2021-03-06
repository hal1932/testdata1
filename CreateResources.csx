#r "System.dll"
#r "System.Xml.Linq.dll"
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;

var rootDir = CreateDirectory(Path.GetFullPath("resources"));
var modelRootDir = CreateDirectory(Path.Combine(rootDir.FullName, "models"));

const int cModelCount = 1000;
const int cCommonTextureCount = 300;
var cTextureCountPerModel = new KeyValuePair<int, int>(3, 7);
var cCommonTextureCountPerModel = new KeyValuePair<int, int>(0, 3);

var cModelSize = new KeyValuePair<int, int>(512 * 1024, 3 * 1024 * 1024);
var cTextureSize = new KeyValuePair<int, int>(512 * 1024, 3 * 1024 * 1024);

var modelDic = new Dictionary<FileInfo, List<FileInfo>>();
var rand = new Random(1234567890);

var lockObj = new object();

// モデル、個別テクスチャ
Parallel.For(0, cModelCount, (i) =>
{
    // mdl
    var name = "model" + i.ToString("D4");
    var dir = CreateDirectory(Path.Combine(modelRootDir.FullName, name));

    var modelfile = CreateBinaryFile(Path.Combine(dir.FullName, name) + ".mdl", rand.Next(cModelSize.Key, cModelSize.Value));

    // tex
    var textures = new List<FileInfo>();
    var texdir = CreateDirectory(Path.Combine(dir.FullName, "textures"));
    var texCount = rand.Next(cTextureCountPerModel.Key, cTextureCountPerModel.Value);
    for (var j = 0; j < texCount; ++j)
    {
        var texname = name + "_" + j.ToString("D2") + ".tex";
        var texpath = Path.Combine(texdir.FullName, texname);
        var texfile = CreateBinaryFile(texpath, rand.Next(cTextureSize.Key, cTextureSize.Value));

        textures.Add(texfile);
    }

    lock(lockObj)
    {
        modelDic[modelfile] = textures;
    }
});

// 共通テクスチャ
var commonDir = Path.Combine(modelRootDir.FullName, "common");
var commonTexDir = CreateDirectory(Path.Combine(commonDir, "textures"));
var commonTextures = new List<FileInfo>();
Parallel.For(0, cCommonTextureCount, (i) =>
{
    var texpath = Path.Combine(commonTexDir.FullName, "common" + i.ToString("D4") + ".tex");
    var tex = CreateBinaryFile(texpath, rand.Next(cTextureSize.Key, cTextureSize.Value));

    lock(lockObj)
    {
        commonTextures.Add(tex);
    }
});
Parallel.ForEach(modelDic, (item) =>
{
    var texCount = rand.Next(cCommonTextureCountPerModel.Key, cCommonTextureCountPerModel.Value);
    for (var i = 0; i < texCount; ++i)
    {
        var texIdx = rand.Next(commonTextures.Count);
        item.Value.Add(commonTextures[texIdx]);
    }
});

// モデル定義ファイル
Parallel.ForEach(modelDic, (item) =>
{
    var model = item.Key;
    var textures = item.Value;

    // mdldef
    var defPath = model.FullName.Replace(".mdl", ".mdldef");
    var defDir = model.Directory.FullName;

    var xml = new XElement("ModelDef",
        new XElement("Model",
            new XElement("Path", CalcRelativePath(model.FullName, defDir))),
        new XElement("Textures"));
    var texElem = xml.Element("Textures");
    foreach (var tex in textures)
    {
        texElem.Add(new XElement("Texture",
            new XElement("Path", CalcRelativePath(tex.FullName, defDir))));
    }

    xml.Save(defPath);
});

DirectoryInfo CreateDirectory(string path)
{
    path = Path.GetFullPath(path);
    var dir = new DirectoryInfo(path);
    if (dir.Exists)
    {
        Directory.Delete(dir.FullName, true);
    }
    Directory.CreateDirectory(dir.FullName);
    return dir;
}

string CalcRelativePath(string path, string root)
{
    var relativeUrl = new Uri(root + Path.DirectorySeparatorChar)
        .MakeRelativeUri(new Uri(path));
    return relativeUrl.ToString();
}

FileInfo CreateBinaryFile(string path, int size)
{
    var rand = new Random();

    var dataSize = 1024 * 1024;
    var data = new byte[size < dataSize ? size : dataSize];

    using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write))
    using (var writer = new BinaryWriter(stream))
    {
        while (true)
        {
            rand.NextBytes(data);
            writer.Write(data);

            size -= dataSize;
            if (size <= 0)
            {
                break;
            }
            else if (size < dataSize)
            {
                data = new byte[size];
            }
        }
    }

    return new FileInfo(path);
}
