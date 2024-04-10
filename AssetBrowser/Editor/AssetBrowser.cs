using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using System.Text;
using static PlasticPipe.Server.MonitorStats;
using static TreeEditor.TextureAtlas;
using static UnityEditor.Rendering.CameraUI;

public class AssetBrowser : EditorWindow
{
    string workDir = "Assets/AssetBrowser/Import Library";
    AssetSource source = AssetSource.AmbientCG;

    bool startup = true, showPreviews = true, createMaterials = true;

    AssetType searchType = AssetType.Material;
    AmbCGSort AmbFilter = AmbCGSort.Popular;
    TexResolution texRes = TexResolution._2k;
    SkyResolution skyRes = SkyResolution._4k;
    Shader matShader;

    string searchPhrase;

    int loading; // 0 = ready, 1 = fetching data, 2 = fetching previews

    Vector2 scrollPos;

    int resultCount, resultLoad, offset;

    List<Result> results = new();
    List<Texture2D> previews = new(), tempPreviews = new();

    enum AssetSource
    {
        AmbientCG,
        PolyHaven
    }

    enum AssetType
    {
        All,
        Material,
        Decal,
        Skybox
    }

    enum AmbCGSort
    {
        Latest,
        Popular,
        Downloads,
        Alphabetically
    }

    enum TexResolution
    {
        _1k,
        _2k,
        _4k,
        _8k
    }

    enum SkyResolution
    {
        _1k,
        _4k,
        _8k
    }

    struct Result
    {
        public string name, shortName;
        public Texture2D preview;
        public string download;
        public string previewUrl;
        public bool previewable;
        public string format;
    }

    [MenuItem("Tools/Asset Browser")]
    public static void Init()
    {
        EditorWindow.GetWindow(typeof(AssetBrowser));
    }

    private void OnEnable()
    {
        //  sets the default material shader to be correct with current render pipeline asset
        switch (GraphicsSettings.renderPipelineAsset.GetType().Name)
        {
            default:
                {
                    matShader = matShader = Shader.Find("Standard");
                    break;
                }
            case "UniversalRenderPipelineAsset":
                {
                    matShader = Shader.Find("Universal Render Pipeline/Lit");
                    break;
                }
            case "HDRenderPipelineAsset":
                {
                    matShader = Shader.Find("HDRenderPipeline/Lit");
                    break;
                }
        }
    }

    private void OnDisable()
    {
        ClearCache();
    }

    void ClearCache()
    {
        if (Directory.Exists("BrowserCache")) { Directory.Delete("BrowserCache", true); }
        Directory.CreateDirectory("BrowserCache");
    }

    //  renders gui each frame
    private void OnGUI()
    {
        if (startup) {
            ShowStartup();
            return;
        }

        EditorGUILayout.BeginVertical();
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        switch (source)
        {
            case AssetSource.AmbientCG: {
                    ShowAmbientCG();
                    break;
                }
            case AssetSource.PolyHaven: {
                    ShowPolyHaven();
                    break;
                }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    //  startup panel
    void ShowStartup()
    {
        GUILayout.Label("Working Directory:");

        workDir = GUILayout.TextField(workDir);

        if (GUILayout.Button("Set to selected folder"))
        {
            workDir = GetSelectedFolder();
        }

        GUILayout.Space(10);

        GUILayout.Label("Asset Source:");

        source = (AssetSource)EditorGUILayout.EnumPopup(source);

        GUILayout.Space(10);

        showPreviews = GUILayout.Toggle(showPreviews, "Load Image Previews");

        GUILayout.Space(10);

        //  TODO:
        //  TEXTURE RESOLUTIONS & FORMATS?

        GUILayout.Label("Texture Resolution:");

        texRes = (TexResolution)EditorGUILayout.EnumPopup(texRes);

        GUILayout.Space(10);

        GUILayout.Label("Skybox Resolution:");

        skyRes = (SkyResolution)EditorGUILayout.EnumPopup(skyRes);

        GUILayout.Space(10);

        createMaterials = GUILayout.Toggle(createMaterials, "Generate Materials");

        if (createMaterials) {
            GUILayout.Space(10);

            GUILayout.Label("Shader:");

            matShader = (Shader)EditorGUILayout.ObjectField(matShader, typeof(Shader));

            /*//shader field
            ScriptableObject scriptableObj = this;
            SerializedObject serialObj = new SerializedObject(scriptableObj);
            SerializedProperty serialJson = serialObj.FindProperty("matShader");

            EditorGUILayout.PropertyField(serialJson, true);
            serialObj.ApplyModifiedProperties();
            //-----*/
        }

        GUILayout.Space(25);

        if (GUILayout.Button("Start Browsing"))
        {
            startup = false;
        }
        GUILayout.Space(15);
        GUILayout.Label("( V.0.7 )");
        GUILayout.Label("WORK IN PROGRESS\nSome features are currently unfinished.\nCheck for updates or help with development here:");
        if (GUILayout.Button("Github Page"))
        {
            Application.OpenURL("https://github.com/Swagguy47/UnityAssetBrowser");
        }
    }

    //  AmbientCG panel
    void ShowAmbientCG()
    {
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        DisplayLogo("Assets/Resources/Sources/AmbCG.png");

        GUILayout.Space(5f);

        DisplaySearchBar();

        AmbFilter = (AmbCGSort)EditorGUILayout.EnumPopup(AmbFilter);

        GUILayout.Space(10);

        if (GUILayout.Button("Search") && loading == 0)
        {
            offset = 0;
            SearchAmbCG();
        }

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        GUILayout.Label("Displaying: (" + resultLoad + " / " + resultCount + ") results.");

        if (loading == 2)
            GUILayout.Label("Loading previews...");

        DisplayResults();

        if (loading == 1)
            GUILayout.Label("Loading data...");

        if(offset != 0)
            if (GUILayout.Button("Previous Page") && loading == 0) {
                offset--;
                SearchAmbCG();
            }

        if (resultCount == 180)
            if (GUILayout.Button("Next Page") && loading == 0) {
                offset++;
                SearchAmbCG();
            }
            
    }

    //  PolyHaven panel
    void ShowPolyHaven()
    {
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        DisplayLogo("Assets/Resources/Sources/PolyHaven.png");

        GUILayout.Space(5f);

        DisplaySearchBar();

        GUILayout.Space(10);

        if (GUILayout.Button("Search"))
        {
            SearchPolyHaven();
        }

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        if (loading == 2)
            GUILayout.Label("Loading previews...");

        DisplayResults();

        if (loading == 1)
            GUILayout.Label("PolyHaven is currently unsupported."); //GUILayout.Label("Loading data...");
            
    }

    void DisplayResults()
    {
        int i = 0;
        int toolbarInt = 0;

        int toolbarWidth = (int)position.width / 100;
        //Debug.Log(toolbarWidth);

        foreach(Result asset in results)
        {

            //  get image
            string filename = "BrowserCache/" + asset.shortName + ".jpg";

            Texture2D tex = new(1, 1); // Create an empty Texture;
            tempPreviews.Add(tex);

            if (previews.Count > i)
                tex = previews[i];
            else if (File.Exists(filename) && showPreviews)
            {
                var rawData = System.IO.File.ReadAllBytes(filename);
                tex.LoadImage(rawData);
                previews.Add(tex);
            }

            //Texture2D tex = (Texture2D)AssetDatabase.LoadAssetAtPath("BrowserCache/" + asset.shortName + ".jpg", typeof(Texture2D));
            if(toolbarInt == 0) {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
            }

            GUIContent _content;

            float imageWidth = EditorGUIUtility.currentViewWidth / toolbarWidth;
            float imageHeight = imageWidth * tex.height / tex.width;

            Rect rect = GUILayoutUtility.GetRect(imageWidth, imageHeight);

            _content = new GUIContent(tex.width == 1 ? asset.name : "", tex, asset.name); // file name in the resources folder without the (.png) extension
            if (GUI.Button(rect, _content)) { //(Texture2D)AssetDatabase.LoadAssetAtPath("BrowserCache/" + asset.download + ".jpg", typeof(Texture2D))
                if (Event.current.button == 1)  // right click to open url
                    Application.OpenURL(asset.download);
                else   //   left click to import automatically
                    ImportAsset(asset.download, asset.name);
            }

            i++;
            toolbarInt++;
            if (toolbarInt == toolbarWidth || i == results.Count) { 
                toolbarInt = 0; 
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }
    }

    //  name is pointless other than editor logging
    void ImportAsset(string link, string name)
    {
        //  PLACEHOLDER
        switch (source)
        {
            case AssetSource.AmbientCG: {
                    Debug.Log("<color=yellow>Importing " + name + "...</color>\nFrom: " + link);
                    EditorCoroutineUtility.StartCoroutine(ImportAmbCG(link, name), this);
                    break;
                }
            case AssetSource.PolyHaven: {
                    Application.OpenURL(link);
                    break;
                }
        }
    }

    IEnumerator ImportAmbCG(string url, string name)
    {
        UnityWebRequest www = UnityWebRequest.Get(url);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            //Debug.Log(www.error);
        }
        else
        {
            string[] mainBody = SplitString("<main>", www.downloadHandler.text);

            string[] downloadButtons = SplitString("<div class=\'DownloadButtons\'>", mainBody[1]);

            for (int i = 0; i < downloadButtons.Length; i++)
            {
                if (i != 0)
                {
                    downloadButtons[i] = SplitString("<a title=\"📂 Contents:", downloadButtons[i])[1];

                    downloadButtons[i] = SplitString("href=", downloadButtons[i])[1];

                    downloadButtons[i] = downloadButtons[i].Split("\"".ToCharArray())[1];
                }
            }

            EditorCoroutineUtility.StartCoroutine(DownloadAmbCG(downloadButtons[1], name), this);
        }
    }

    IEnumerator DownloadAmbCG(string url, string name)
    {

        string directory = workDir + "/AmbientCG/" + name;

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        WebClient www = new WebClient();
        Uri uri = new Uri(url);
        www.Encoding = Encoding.UTF8;
        www.DownloadProgressChanged += new DownloadProgressChangedEventHandler((sender, data) => {
            //Debug.Log("here you can check progress");
        });
        www.DownloadFileCompleted += new System.ComponentModel.AsyncCompletedEventHandler((sender, data) => {
            //Debug.Log("here you can check complete");
            www.Dispose();

            LoadZipFile(directory + "/download.zip", directory);

            AssetDatabase.Refresh();

            Debug.Log("<color=lime>" + name + " is ready!</color>\n" + directory);
        });
        www.DownloadFileAsync(uri, directory + "/download.zip");

        yield return null;
    }


    //  shows source logo image
    void DisplayLogo(string path)
    {
        Texture2D cover = (Texture2D)AssetDatabase.LoadAssetAtPath(path, typeof(Texture2D));

        float imageWidth = EditorGUIUtility.currentViewWidth - 40;
        float imageHeight = imageWidth * cover.height / cover.width;

        Rect rect = GUILayoutUtility.GetRect(imageWidth, imageHeight);

        GUI.DrawTexture(rect, cover, ScaleMode.ScaleToFit);
    }

    //  shows search area
    void DisplaySearchBar()
    {
        source = (AssetSource)EditorGUILayout.EnumPopup(source);

        searchType = (AssetType)EditorGUILayout.EnumPopup(searchType);

        searchPhrase = GUILayout.TextField(searchPhrase);
    }

    void CleanLeaks()
    {
        foreach(Texture2D preview in previews)
            DestroyImmediate(preview);
        foreach (Texture2D tempPrev in tempPreviews)
            DestroyImmediate(tempPrev);
        foreach (Result result in results)
            DestroyImmediate(result.preview);

        //  last resort
        EditorUtility.UnloadUnusedAssetsImmediate();
        Resources.UnloadUnusedAssets();
    }

    //  scrape data from AmbientCG
    void SearchAmbCG()
    {
        CleanLeaks();
        results.Clear();
        previews.Clear();
        tempPreviews.Clear();
        loading = 1;
        resultCount = 0;
        resultLoad = 0;

        ClearCache();

        string type = "";
        switch (searchType)
        {
            case AssetType.All : {
                break;
            }
            case AssetType.Material:
                {
                    type = "Material";
                    break;
                }
            case AssetType.Decal:
                {
                    type = "Decal";
                    break;
                }
            case AssetType.Skybox:
                {
                    type = "HDRI";
                    break;
                }
        }

        string sort = "";
        switch (AmbFilter)
        {
            case AmbCGSort.Latest: {
                    sort = "Latest";
                    break;
                }
            case AmbCGSort.Popular:
                {
                    sort = "Popular";
                    break;
                }
            case AmbCGSort.Downloads:
                {
                    sort = "Downloads";
                    break;
                }
            case AmbCGSort.Alphabetically:
                {
                    sort = "Alphabet";
                    break;
                }
        }

        string url = "https://ambientcg.com/list?q=" + searchPhrase + "+&type=" + type + "&sort=" + sort + "&offset=" + (180 * offset); //    example: https://ambientcg.com/list?q=dirt+&type=Material&sort=Popular

        //Debug.Log(url);

        EditorCoroutineUtility.StartCoroutine(AmbCGData(url), this);
    }

    IEnumerator AmbCGData(string url)
    {
        UnityWebRequest www = UnityWebRequest.Get(url);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            //Debug.Log(www.error);
        }
        else
        {
            string[] mainBody = SplitString("<main>", www.downloadHandler.text);
            string[] assetBoxes = SplitString("<div class=\"AssetBox\"", mainBody[1]);

            resultCount = assetBoxes.Length - 1;

            foreach (string asset in assetBoxes)
            {
                Result currentResult = new();

                string partialName = asset.Split('\n')[0];

                if (partialName.Length != 0)
                {
                    currentResult.name = partialName.Substring(11, partialName.Length - 11);

                    //Debug.Log(currentResult.name);

                    string shortName = currentResult.name.Split('(')[0].Replace(" ", string.Empty);

                    currentResult.shortName= shortName;

                    currentResult.download = "https://ambientcg.com/view?id=" + shortName;

                    //Debug.Log(currentResult.download);

                    currentResult.previewUrl = "https://acg-media.struffelproductions.com/file/ambientCG-Web/media/thumbnail/256-JPG-242424/" + shortName + ".jpg"; 

                    /*UnityWebRequest tempPrev = UnityWebRequest.Get("https://acg-media.struffelproductions.com/file/ambientCG-Web/media/thumbnail/1024-JPG-242424/" + currentResult.download);

                    yield return tempPrev.SendWebRequest();

                    if (www.result == UnityWebRequest.Result.Success) {
                        //Texture2D texture = new Texture2D(1024, 1024);
                        currentResult.tempPrev = ((DownloadHandlerTexture)tempPrev.downloadHandler).texture;
                    }*/

                    results.Add(currentResult);
                    resultLoad++;
                }
            }

            //Debug.Log("Fetched all "+ results.Count +" results");
            if (!showPreviews)
            {
                loading = 0;
                yield break;
            }

            loading = 2;

            for (int i = 0; i < results.Count; i++)
            {
                EditorCoroutineUtility.StartCoroutine(FetchImage(results[i].previewUrl, results[i].shortName), this);
            }

            loading = 0;
        }
    }

    IEnumerator FetchImage(string url, string shortName)
    {
        UnityWebRequest web = UnityWebRequestTexture.GetTexture(url);

        yield return web.SendWebRequest();

        if (web.result == UnityWebRequest.Result.Success)
        {
            Texture2D myTexture = DownloadHandlerTexture.GetContent(web);

            byte[] tex = myTexture.EncodeToJPG();

            FileStream stream = new("BrowserCache/" + shortName + ".jpg", FileMode.OpenOrCreate, FileAccess.ReadWrite);
            BinaryWriter writer = new(stream);
            for (int j = 0; j < tex.Length; j++)
                writer.Write(tex[j]);

            writer.Close();
            stream.Close();

            //previews.Add(myTexture);
        }
        else
        {
            //Debug.Log(results[i].previewUrl);
            Debug.Log(web.error);
        }
    }

    public string[] SplitString(string needle, string haystack)
    {
        //This will look for NEEDLE in HAYSTACK and return an array of split strings.
        //NOTE: If the returned array has a length of 1 (meaning it only contains
        //        element [0]) then that means NEEDLE was NOT found.

        return haystack.Split(new string[] { needle }, System.StringSplitOptions.None);

    }

    //  scrape data from PolyHaven
    void SearchPolyHaven()
    {
        results.Clear();
        previews.Clear();
        loading = 0;

        ClearCache();
    }

    //  gets selected folder
    string GetSelectedFolder()
    {
        return null;
    }

    void LoadZipFile(string FilePath, string outputPath)
    {
        if (System.IO.File.Exists(FilePath) == false)
        {
            Debug.Log("File does not exist! " + FilePath);
            return;
        }

        // Read file
        FileStream fs = null;
        try
        {
            fs = new FileStream(FilePath, FileMode.Open);
        }
        catch
        {
            Debug.Log("GameData file open exception: " + FilePath);
        }

        if (fs != null)
        {
            try
            {
                // Read zip file
                ZipFile zf = new ZipFile(fs);
                int numFiles = 0;

                if (zf.TestArchive(true) == false)
                {
                    Debug.Log("Zip file failed integrity check!");
                    zf.IsStreamOwner = false;
                    zf.Close();
                    fs.Close();
                }
                else
                {
                    string shortTexName = "";
                    foreach (ZipEntry zipEntry in zf)
                    {
                        // Ignore directories
                        if (!zipEntry.IsFile)
                            continue;

                        String entryFileName = zipEntry.Name;

                        string texName = outputPath.Substring(workDir.Length + 1 + "/AmbientCG".Length).Replace(" ", "").Split("(")[0];

                        // Skip .DS_Store files (these appear on OSX)
                        if (entryFileName.Contains("DS_Store"))
                            continue;
                        if (entryFileName.Contains("usdc"))
                            continue;
                        if (entryFileName.Contains(".mtlx"))
                            continue;
                        if (entryFileName.Contains("NormalDX")) //  unity uses OpenGL format normals (I think)
                            continue;
                        if(entryFileName ==  texName + ".png")
                        {
                            shortTexName = texName;
                            continue;
                        }

                        //Debug.Log(entryFileName + "\n" + outputPath.Substring(workDir.Length + 1 + "/AmbientCG".Length).Replace(" ", "") + ".png");

                        //Debug.Log("Unpacking zip file entry: " + entryFileName);

                        byte[] buffer = new byte[4096];     // 4K is optimum
                        Stream zipStream = zf.GetInputStream(zipEntry);

                        // Manipulate the output filename here as desired.
                        string fullZipToPath =  outputPath + "/" + Path.GetFileName(entryFileName);//"c:\\" + Path.GetFileName(entryFileName);

                        // Unzip file in buffered chunks. This is just as fast as unpacking to a buffer the full size
                        // of the file, but does not waste memory.
                        // The "using" will close the stream even if an exception occurs.
                        using (FileStream streamWriter = File.Create(fullZipToPath))
                        {
                            StreamUtils.Copy(zipStream, streamWriter, buffer);
                        }
                        numFiles++;
                    }

                    zf.IsStreamOwner = false;
                    zf.Close();
                    fs.Close();

                    //  Delete zip file
                    File.Delete(FilePath);

                    GenerateMaterial(outputPath, shortTexName);
                }
            }
            catch
            {
                Debug.Log("Zip file error!");
            }
        }
    }

    //  makes material and fills texture slots
    void GenerateMaterial(string path, string textureName)
    {
        if (!createMaterials)
            return;

        //Debug.Log("Path: " + path + "\nname: " + textureName);

        if (AssetDatabase.LoadAssetAtPath(path + ".mat", typeof(Material)) != null)
            AssetDatabase.DeleteAsset(path + ".mat");

        var material = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(material, path + ".mat");
        Material mat = (Material)AssetDatabase.LoadAssetAtPath(path + ".mat", typeof(Material));

        mat.shader = matShader;
        mat.SetFloat("_Glossiness", 0.3f);

        AssetDatabase.Refresh();

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Color" + ".jpg", typeof(Texture2D)) != null)
        {
            mat.mainTexture = (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Color" + ".jpg", typeof(Texture2D));
        }

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_NormalGL" + ".jpg", typeof(Texture2D)) != null)
        {
            TextureImporter A = (TextureImporter)AssetImporter.GetAtPath(path + "/" + textureName + "_1K-JPG_NormalGL" + ".jpg");
            A.textureType = TextureImporterType.NormalMap;

            AssetDatabase.ImportAsset(path + "/" + textureName + "_1K-JPG_NormalGL" + ".jpg");

            mat.SetTexture("_BumpMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_NormalGL" + ".jpg", typeof(Texture2D)));
        }

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Roughness" + ".jpg", typeof(Texture2D)) != null)
        {
            mat.SetTexture("_SpecGlossMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Roughness" + ".jpg", typeof(Texture2D)));
        }

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Metalness" + ".jpg", typeof(Texture2D)) != null)
        {
            mat.SetTexture("_MetallicGlossMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Metalness" + ".jpg", typeof(Texture2D)));
        }
        else if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Roughness" + ".jpg", typeof(Texture2D)) != null) //  if no metallic exists just use roughness
        {
            mat.SetTexture("_MetallicGlossMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Roughness" + ".jpg", typeof(Texture2D)));
        }

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Emission" + ".jpg", typeof(Texture2D)) != null)
        {
            mat.SetTexture("_EmissionMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Emission" + ".jpg", typeof(Texture2D)));
        }

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Displacement" + ".jpg", typeof(Texture2D)) != null)
        {
            mat.SetTexture("_ParallaxMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_Displacement" + ".jpg", typeof(Texture2D)));
        }

        if (AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_AmbientOcclusion" + ".jpg", typeof(Texture2D)) != null)
        {
            mat.SetTexture("_OcclusionMap", (Texture2D)AssetDatabase.LoadAssetAtPath(path + "/" + textureName + "_1K-JPG_AmbientOcclusion" + ".jpg", typeof(Texture2D)));
        }
    }
}
