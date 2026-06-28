using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
﻿using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Devices;

    [Export(typeof(IEquipmentProvider))]
    public class FujiCameraProvider : IEquipmentProvider<ICamera>
    {
        private readonly IFujiCameraFactory _factory;

        public string Name => "Fujifilm";

        [ImportingConstructor]
        public FujiCameraProvider(IFujiCameraFactory factory)
        {
            _factory = factory;
        }

        public IList<ICamera> GetEquipment()
        {
            try 
            { 
                var descriptors = _factory.GetAvailableCamerasAsync(CancellationToken.None).GetAwaiter().GetResult();

                var cameras = new List<ICamera>();
                foreach (var descriptor in descriptors)
                {
                    cameras.Add(_factory.CreateGenericCamera(descriptor));
                }

                return cameras;
            } 
            catch (Exception ex)
            {
                Logger.Error($"Fujifilm camera discovery failed: {ex}");
                return new List<ICamera>();
            }
        }
    }
