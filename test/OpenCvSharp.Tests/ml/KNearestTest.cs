using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenCvSharp;
using OpenCvSharp.ML;

namespace OpenCvSharp.Tests.ML
{
    [TestFixture]
    public class KNearestTest : TestBase
    {
        [Test]
        public void RunTest()
        {
            float[] trainFeaturesData =
            {
                 2,2,2,2,
                 3,3,3,3,
                 4,4,4,4,
                 5,5,5,5,
                 6,6,6,6,
                 7,7,7,7
            };
            var trainFeatures = new Mat(6, 4, MatType.CV_32F, trainFeaturesData);

            int[] trainLabelsData = { 2, 3, 4, 5, 6, 7 };
            var trainLabels = new Mat(1, 6, MatType.CV_32S, trainLabelsData);

            var kNearest = KNearest.Create();
            kNearest.Train(trainFeatures, SampleTypes.RowSample, trainLabels);

            float[] testFeatureData = { 3, 3, 3, 3 };
            var testFeature = new Mat(1, 4, MatType.CV_32F, testFeatureData);

            const int k = 1;
            var results = new Mat();
            var neighborResponses = new Mat();
            var dists = new Mat();
            var detectedClass = (int)kNearest.FindNearest(testFeature, k, results, neighborResponses, dists);

            Assert.AreEqual(3, detectedClass);
        }

        [Test]
        public void SaveLoadTest()
        {
            float[] trainFeaturesData =
            {
                 2,2,2,2,
                 3,3,3,3,
                 4,4,4,4,
                 5,5,5,5,
                 6,6,6,6,
                 7,7,7,7
            };
            var trainFeatures = new Mat(6, 4, MatType.CV_32F, trainFeaturesData);

            int[] trainLabelsData = { 2, 3, 4, 5, 6, 7 };
            var trainLabels = new Mat(1, 6, MatType.CV_32S, trainLabelsData);

            const string fileName = "knearest.yml";
            if (File.Exists(fileName))
                File.Delete(fileName);

            using (var model = KNearest.Create())
            {
                model.Train(trainFeatures, SampleTypes.RowSample, trainLabels);

                model.Save(fileName);
            }

            FileAssert.Exists(fileName);

            string content = File.ReadAllText(fileName);
            //Console.WriteLine(content);

            Assert.DoesNotThrow(() =>
            {
                using (var model2 = KNearest.Load(fileName)) { }
            });
            Assert.DoesNotThrow(() =>
            {
                using (var model2 = KNearest.LoadFromString(content)) { }
            });
        }
    }
}

