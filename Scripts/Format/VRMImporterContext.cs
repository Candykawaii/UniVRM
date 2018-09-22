﻿using DepthFirstScheduler;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using UniGLTF;
using UnityEngine;


namespace VRM
{
    public class VRMImporterContext : ImporterContext
    {
        const string HUMANOID_KEY = "humanoid";
        const string MATERIAL_KEY = "materialProperties";

        public VRMImporterContext(UnityPath gltfPath = default(UnityPath)) : base(gltfPath)
        {
        }

        public static VRMImporterContext Load(string path)
        {
            var context = new VRMImporterContext(UniGLTF.UnityPath.FromFullpath(path));
            context.ParseGlb(File.ReadAllBytes(path));
            context.Load();
            return context;
        }

        public static VRMImporterContext Load(Byte[] bytes)
        {
            var context = new VRMImporterContext();
            context.ParseGlb(bytes);
            context.Load();
            return context;
        }

        public override void Load()
        {
            MaterialImporter = new VRMMaterialImporter(this, glTF_VRM_Material.Parse(Json));
            base.Load();
            OnLoadModel();
        }

        protected override Schedulable<GameObject> LoadAsync(bool show)
        {
            return Schedulable.Create()
                .AddTask(Scheduler.ThreadPool, () =>
                {
                    using (MeasureTime("glTF_VRM_Material.Parse"))
                    {
                        return glTF_VRM_Material.Parse(Json);
                    }
                })
                .ContinueWith(Scheduler.MainThread, gltfMaterials =>
                {
                    using (MeasureTime("new VRMMaterialImporter"))
                    {
                        MaterialImporter = new VRMMaterialImporter(this, gltfMaterials);
                    }
                })
                .OnExecute(Scheduler.ThreadPool, parent =>
                {
                    // textures
                    for (int i = 0; i < GLTF.textures.Count; ++i)
                    {
                        var index = i;
                        parent.AddTask(Scheduler.MainThread,
                                () =>
                                {
                                    using (MeasureTime("texture.Process"))
                                    {
                                        var texture = new TextureItem(GLTF, index);
                                        texture.Process(GLTF, Storage);
                                        return texture;
                                    }
                                })
                            .ContinueWith(Scheduler.ThreadPool, x => AddTexture(x));
                    }
                })
                .ContinueWithCoroutine(Scheduler.MainThread, () => LoadMaterials())
                .OnExecute(Scheduler.ThreadPool, parent =>
                {
                    // meshes
                    var meshImporter = new MeshImporter();
                    for (int i = 0; i < GLTF.meshes.Count; ++i)
                    {
                        var index = i;
                        parent.AddTask(Scheduler.ThreadPool,
                                () =>
                                {
                                    using (MeasureTime("ReadMesh"))
                                    {
                                        return meshImporter.ReadMesh(this, index);
                                    }
                                })
                        .ContinueWith(Scheduler.MainThread, x =>
                        {
                            using (MeasureTime("BuildMesh"))
                            {
                                return MeshImporter.BuildMesh(this, x);
                            }
                        })
                        .ContinueWith(Scheduler.ThreadPool, x => Meshes.Add(x))
                        ;
                    }
                })
                .ContinueWithCoroutine(Scheduler.MainThread, () =>
                {
                    using (MeasureTime("LoadNodes"))
                    {
                        return LoadNodes();
                    }
                })
                .ContinueWithCoroutine(Scheduler.MainThread, () =>
                {
                    using (MeasureTime("BuildHierarchy"))
                    {
                        return BuildHierarchy();
                    }
                })
                .ContinueWith(Scheduler.CurrentThread, _ =>
                {
                    //using (MeasureTime("OnLoadModel"))
                    {
                        OnLoadModel();
                        return Unit.Default;
                    }
                })
                .ContinueWith(Scheduler.CurrentThread,
                    _ =>
                    {
                        Root.name = "VRM";
                        Debug.Log(GetSpeedLog());
                        return Root;
                    });
        }


        #region OnLoad
        void OnLoadModel()
        {
            using (MeasureTime("VRM LoadMeta"))
            {
                LoadMeta();
            }

            using (MeasureTime("VRM LoadHumanoid"))
            {
                LoadHumanoid();
            }

            using (MeasureTime("VRM LoadBlendShapeMaster"))
            {
                LoadBlendShapeMaster();
            }
            using (MeasureTime("VRM LoadSecondary"))
            {
                VRMSpringUtility.LoadSecondary(Root.transform, Nodes,
                GLTF.extensions.VRM.secondaryAnimation);
            }
            using (MeasureTime("VRM LoadFirstPerson"))
            {
                LoadFirstPerson();
            }
        }

        void LoadMeta()
        {
            var meta = ReadMeta();
            if (meta.Thumbnail == null)
            {
                /*
                // 作る
                var lookAt = Root.GetComponent<VRMLookAtHead>();
                var thumbnail = lookAt.CreateThumbnail();
                thumbnail.name = "thumbnail";
                meta.Thumbnail = thumbnail;
                Textures.Add(new TextureItem(thumbnail));
                */
            }
            var _meta = Root.AddComponent<VRMMeta>();
            _meta.Meta = meta;
            Meta = meta;
        }

        void LoadFirstPerson()
        {
            var firstPerson = Root.AddComponent<VRMFirstPerson>();

            var gltfFirstPerson = GLTF.extensions.VRM.firstPerson;
            if (gltfFirstPerson.firstPersonBone != -1)
            {
                firstPerson.FirstPersonBone = Nodes[gltfFirstPerson.firstPersonBone];
                firstPerson.FirstPersonOffset = gltfFirstPerson.firstPersonBoneOffset;
            }
            else
            {
                // fallback
                firstPerson.SetDefault();
            }
            firstPerson.TraverseRenderers(this);

            // LookAt
            var lookAtHead = Root.AddComponent<VRMLookAtHead>();
            lookAtHead.OnImported(this);
        }

        void LoadBlendShapeMaster()
        {
            BlendShapeAvatar = ScriptableObject.CreateInstance<BlendShapeAvatar>();
            BlendShapeAvatar.name = "BlendShape";

            var blendShapeList = GLTF.extensions.VRM.blendShapeMaster.blendShapeGroups;
            if (blendShapeList != null && blendShapeList.Count > 0)
            {
                foreach (var x in blendShapeList)
                {
                    BlendShapeAvatar.Clips.Add(LoadBlendShapeBind(x));
                }
            }

            var proxy = Root.AddComponent<VRMBlendShapeProxy>();
            BlendShapeAvatar.CreateDefaultPreset();
            proxy.BlendShapeAvatar = BlendShapeAvatar;
        }

        BlendShapeClip LoadBlendShapeBind(glTF_VRM_BlendShapeGroup group)
        {
            var asset = ScriptableObject.CreateInstance<BlendShapeClip>();
            var groupName = group.name;
            var prefix = "BlendShape.";
            while (groupName.StartsWith(prefix))
            {
                groupName = groupName.Substring(prefix.Length);
            }
            asset.name = "BlendShape." + groupName;

            if (group != null)
            {
                asset.BlendShapeName = groupName;
                asset.Preset = EnumUtil.TryParseOrDefault<BlendShapePreset>(group.presetName);
                if (asset.Preset == BlendShapePreset.Unknown)
                {
                    // fallback
                    asset.Preset = EnumUtil.TryParseOrDefault<BlendShapePreset>(group.name);
                }
                asset.Values = group.binds.Select(x =>
                {
                    var mesh = Meshes[x.mesh].Mesh;
                    var node = Root.transform.Traverse().First(y => y.GetSharedMesh() == mesh);
                    var relativePath = UniGLTF.UnityExtensions.RelativePathFrom(node, Root.transform);
                    return new BlendShapeBinding
                    {
                        RelativePath = relativePath,
                        Index = x.index,
                        Weight = x.weight,
                    };
                })
                .ToArray();
                asset.MaterialValues = group.materialValues.Select(x =>
                {
                    var value = new Vector4();
                    for (int i = 0; i < x.targetValue.Length; ++i)
                    {
                        switch (i)
                        {
                            case 0: value.x = x.targetValue[0]; break;
                            case 1: value.y = x.targetValue[1]; break;
                            case 2: value.z = x.targetValue[2]; break;
                            case 3: value.w = x.targetValue[3]; break;
                        }
                    }

                    var material = GetMaterials().FirstOrDefault(y => y.name == x.materialName);
                    var propertyName = x.propertyName;
                    if (x.propertyName.EndsWith("_ST_S")
                    || x.propertyName.EndsWith("_ST_T"))
                    {
                        propertyName = x.propertyName.Substring(0, x.propertyName.Length - 2);
                    }

                    var binding = default(MaterialValueBinding?);

                    if (material != null)
                    {
                        try
                        {
                            binding = new MaterialValueBinding
                            {
                                MaterialName = x.materialName,
                                ValueName = x.propertyName,
                                TargetValue = value,
                                BaseValue = material.GetColor(propertyName),
                            };
                        }
                        catch (Exception)
                        {
                            // do nothing
                        }
                    }

                    return binding;
                })
                .Where(x => x.HasValue)
                .Select(x => x.Value)
                .ToArray();
            }

            return asset;
        }

        static String ToHumanBoneName(HumanBodyBones b)
        {
            foreach (var x in HumanTrait.BoneName)
            {
                if (x.Replace(" ", "") == b.ToString())
                {
                    return x;
                }
            }

            throw new KeyNotFoundException();
        }

        static SkeletonBone ToSkeletonBone(Transform t)
        {
            var sb = new SkeletonBone();
            sb.name = t.name;
            sb.position = t.localPosition;
            sb.rotation = t.localRotation;
            sb.scale = t.localScale;
            return sb;
        }

        private void LoadHumanoid()
        {
            AvatarDescription = GLTF.extensions.VRM.humanoid.ToDescription(Nodes);
            AvatarDescription.name = "AvatarDescription";
            HumanoidAvatar = AvatarDescription.CreateAvatar(Root.transform);
            HumanoidAvatar.name = "VrmAvatar";

            var humanoid = Root.AddComponent<VRMHumanoidDescription>();
            humanoid.Avatar = HumanoidAvatar;
            humanoid.Description = AvatarDescription;

            var animator = Root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = Root.AddComponent<Animator>();
            }
            animator.avatar = HumanoidAvatar;
        }
        #endregion

        public UniHumanoid.AvatarDescription AvatarDescription;
        public Avatar HumanoidAvatar;
        public BlendShapeAvatar BlendShapeAvatar;
        public VRMMetaObject Meta;

        public VRMMetaObject ReadMeta(bool createThumbnail = false)
        {
            var meta = ScriptableObject.CreateInstance<VRMMetaObject>();
            meta.name = "Meta";
            meta.ExporterVersion = GLTF.extensions.VRM.exporterVersion;

            var gltfMeta = GLTF.extensions.VRM.meta;
            meta.Version = gltfMeta.version; // model version
            meta.Author = gltfMeta.author;
            meta.ContactInformation = gltfMeta.contactInformation;
            meta.Reference = gltfMeta.reference;
            meta.Title = gltfMeta.title;

            var thumbnail = GetTexture(gltfMeta.texture);
            if (thumbnail!=null)
            {
                // ロード済み
                meta.Thumbnail = thumbnail.Texture;
            }
            else if (createThumbnail)
            {
                // 作成する(先行ロード用)
                if (gltfMeta.texture >= 0 && gltfMeta.texture < GLTF.textures.Count)
                {
                    var t = new TextureItem(GLTF, gltfMeta.texture);
                    t.Process(GLTF, Storage);
                    meta.Thumbnail = t.Texture;
                }
            }

            meta.AllowedUser = gltfMeta.allowedUser;
            meta.ViolentUssage = gltfMeta.violentUssage;
            meta.SexualUssage = gltfMeta.sexualUssage;
            meta.CommercialUssage = gltfMeta.commercialUssage;
            meta.OtherPermissionUrl = gltfMeta.otherPermissionUrl;

            meta.LicenseType = gltfMeta.licenseType;
            meta.OtherLicenseUrl = gltfMeta.otherLicenseUrl;

            return meta;
        }

#if UNITY_EDITOR
        protected override IEnumerable<UnityEngine.Object> ObjectsForSubAsset()
        {
            foreach (var x in base.ObjectsForSubAsset())
            {
                yield return x;
            }

            yield return AvatarDescription;
            yield return HumanoidAvatar;

            if (BlendShapeAvatar != null && BlendShapeAvatar.Clips != null)
            {
                foreach (var x in BlendShapeAvatar.Clips)
                {
                    yield return x;
                }
            }
            yield return BlendShapeAvatar;

            yield return Meta;
        }

        protected override UnityPath GetAssetPath(UnityPath prefabPath, UnityEngine.Object o)
        {
            if (o is BlendShapeAvatar
                || o is BlendShapeClip)
            {
                var dir = prefabPath.GetAssetFolder(".BlendShapes");
                var assetPath = dir.Child(o.name.EscapeFilePath() + ".asset");
                return assetPath;
            }
            else
            {
                return base.GetAssetPath(prefabPath, o);
            }
        }
#endif
    }
}
