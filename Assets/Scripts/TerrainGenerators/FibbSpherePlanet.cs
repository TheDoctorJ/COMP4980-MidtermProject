using System;
using System.Collections.Generic;
using System.Linq;
using andywiecko.BurstTriangulator;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using static andywiecko.BurstTriangulator.Utilities;


/**
 * This class generates a sphere mesh using the Fibonacci sphere algorithm and Delaunay triangulation.
 * It also includes methods for stereographic projection of points between 2D and 3D. 
 * Then it uses perlin noise to generate some realistic terrain on the sphere.
 * 
 * TODO: I want to tile the sphere, but am confused about the centroid calculation aspect. Will add later.
 * TODO: Also want to add LOD. That's very hard.
 * 
 * 
 */
public class FibbSpherePlanet : MonoBehaviour
{

    List<Vector3> points = new List<Vector3>();
    List<Color> colours = new List<Color>();
    int[] triangles;
    MeshFilter mf;
    // Stores the generated noise values, I'm thinking of seeing if I can reuse it to mix ridged generation in.
    float[] simplexValues;
    int poleIndex;
    [Header("Generation parameters.")]
    // unused right now, but typically would space out your points from the FibbSphere. Unsure how it affects terrain and triangularization.
    [SerializeField] float jitter = 0;
    // Sea Level
    [SerializeField] float seaLevel;
    float approximateSeaRadius = 0;
    float seaNoiseNumber = 0;
    // Planet radius in world units, also used in pnoise.
    [SerializeField] float radius = 1.0f;
    // This is the scale of how high the mountains can go, it shouldn't go very high
    // (especially over 1), as it makes the peaks and valleys to eccentric. This is relative to the radius.
    // higher numba -> more cartoony mountains and valleys.
    [SerializeField] float extremaScale = 0.1f;
    [Header("Colours!")]
    [SerializeField] Gradient terrainGradient;
    [SerializeField] Color deepWater = Color.darkBlue;
    [SerializeField] Color otherWater = Color.darkBlue;


    [Header("Continental Frequencies")]
    [SerializeField]
    List<float> continentFrequencyList = new List<float>()
    {
        1f,2f,4f,8f,16f,32f
    };
    [Header("Continental Amplitudes")]
    [SerializeField]
    List<float> continentAmplitudeList = new List<float>()
    {
        1f, .5f, .25f, .125f, .0625f, .03125f
    };

    [Header("Mountain Parameters")]
    // Mountain needs its own parameter for how much it affects height. What's happening rn is that its absolutely spiking straight up.
    // TODO: Decide if just leave out mountains, I'm not sure how to get it somewhat smooth along the coasts anyways.
    [SerializeField] float mountainStrength = .5f;
    float mountainNoiseNumber = 0;
    [Header("Mountain Noise Frequencies")]
    [SerializeField]
    List<float> mountainFrequencyList = new List<float>()
    {
        1f,2f,4f,8f,16f,32f
    };
    [Header("Mountain Noise Amplitudes")]
    [SerializeField]
    List<float> mountainAmplitudeList = new List<float>()
    {
        1f, .5f, .25f, .125f, .0625f, .03125f
    };


    

    // How many verticies should we use?
    [SerializeField] int vertexStartCount = 8000;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Debug.Log("FibbSpherePlanet script has started.");
        Debug.Log(gameObject.name);

        // Mesh (filter) that the GO is attached to.
        mf = GetComponent<MeshFilter>();
        // trying this out hehe
        mf.mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;


        // So I realized that sphere not going to work. instead what we have to do is generate a point with FibbSpherePlanet and then use delaunay triangulation to generate a mesh from that. Then we can use the diamond square algorithm on that mesh.
        poleIndex = vertexStartCount;
        points = FibbSphere(vertexStartCount);


        // new issue: 3D delaunay is not implementened. Redblob recommends stereographic projection to 2d and go from there, then project back to 3D.
        // This is good, because it means that in the middle step I can run some terrain generation.
        // ungodly poor memory management.
        Vector2[] projectedPoints = StereographicProject2D(points.ToArray());

        using NativeArray<Vector2> projectedPointsNative = new NativeArray<Vector2>(projectedPoints, Allocator.Persistent);

        using var triangulator = new Triangulator<Vector2>(Allocator.Persistent)
        {
            Input = { Positions = projectedPointsNative },
            Settings = { AutoHolesAndBoundary = true }
        };
        triangulator.Run();

        // fill in the hole
        List<int> trangles = new List<int>(triangulator.Output.Triangles.AsArray().ToArray());
        NativeList<int> halfEdges = triangulator.Output.Halfedges;
        int originalCount = trangles.Count;

        // this is the pole point, we're actually gonna change it to be mean later.
        points.Add(new Vector3(0, 0, 1));


        //// store for average
        Vector3 avg = new Vector3(0, 0, 0);
        ushort count = 0;
        List<int> halfEdgeInds = new List<int>(6);
        for (int i = 0; i < halfEdges.Length; i++)
        {
            // we only want to add a trangle on the edge of the hole. skip the ones that are sharing a half edge with another.
            if (halfEdges[i] != -1) continue;
            halfEdgeInds.Add(i);

            count++;
            int a = trangles[i];
            int b = trangles[NextHalfedge(i)];

            avg += points[a];

            trangles.Add(b);
            trangles.Add(a);
            trangles.Add(poleIndex);

        }
        // So the vertex we added might be a little off from the hole. so we take the avg. of the (x,y).
        avg /= count;
        points[poleIndex] = new Vector3(avg.x, avg.y, math.sqrt(1 - math.square(avg.x) - math.square(avg.y)));

        triangles = triangulator.Output.Triangles.AsArray().ToArray();

        Debug.Log("Number of halfedges in total: " + string.Join(", ", halfEdges.Length));

        ApplySimplexNoise();
        ApplyRidgedSimplexNoise();

        mf.mesh.SetVertices(points);
        mf.mesh.SetTriangles(trangles, 0);
        mf.mesh.SetColors(colours);
        // annnnd finally rotate the mesh so that we see the pretty piece thats at the n pole.
        // Explanation: I didn't realize unity has +z coming out of the screen.
        gameObject.transform.rotation = Quaternion.Euler(-90, 0, 0);
        mf.mesh.RecalculateNormals();
    }

    /**
     * Used for mountains
     */
    private void ApplyRidgedSimplexNoise()
    {
        // We could add perlin noise in 2d and stereo project back to 3d, but I think just iterating through again is fine
        // check out this: https://www.redblobgames.com/maps/terrain-from-noise/
        for (int i = 0; i < points.Count; i++)
        {
            // so we're gonna do the layers that redblob does, and see how that works out.
            // Layering works simply, apply perlin (cnoise) with a couple different
            float3 pf = points[i];
            // trying this out, seeing as this is mountain stuff, if our point is at the waterline we skip. Also thinking maybe
            // we should just be checking the spots where we're close to peaks in general? That way we'd leave out everything except big mountains.
            if (simplexValues[i] <= mountainNoiseNumber)
            {
                continue;
            }


            // Updated method: Problem with the exponential method I was doing is that it was really sensitive
            // and generating domes. This makes ridges instead!
            float eSum = 0;
            float ampSum = 0;


            for (int j = 0; j < mountainFrequencyList.Count; j++)
            {
                if (j == 0)
                {
                    float e = mountainAmplitudeList[j] * RidgeNoise(math.normalize(pf) * mountainFrequencyList[j]);
                    eSum += (e);

                }
                else {
                    float e = mountainAmplitudeList[j] * RidgeNoise(math.normalize(pf) * mountainFrequencyList[j]) * eSum;
                    eSum += e;
                }
                ampSum += mountainAmplitudeList[j];
            }
            eSum = eSum / ampSum;
            eSum = math.pow(eSum * 1.2f, 4);
            // redblob uses a fudge factor and exponent to push pull valleys.
            // New issue: cnoise gives a [-1,1]. This is not good for powers. If we add 1 we just get [0,2].
            // so instead we do add 1 divide by 2. [-1,1] -> [0,2] -> [0,1]
            // pnoise = math.saturate((pnoise + 1) * 0.5f);


            // rearranging this was a pain. esum * mountainStrength -> apply how much the mountain noise affects this ridged generated noise, + simplexValue[i] -> the original noise value generated).
            // 1f -> we're always applying as a scale factor, therefore need to start at 1, extremaScale -> Still just smooshing everything.
            float height = 1f + ( eSum* mountainStrength + simplexValues[i]) * extremaScale;
            Vector3 p =  math.normalize(pf) * height*radius;
            points[i] = p;
            colours[i]=SimpleVertexColour(eSum + simplexValues[i]);


        }
    }

    /**
     * Used for the continents
     */
    private void ApplySimplexNoise()
    {

        // So new issue with sea level. turns out that because simplex/perlin isn't a normal distribution, I have to calculate at what radius see level starts.
        // I can't just use a simple assumption over the values.
        simplexValues = new float[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            float3 pf = points[i];


            float pnoise = 0;
            float ampSum = 0;

            for (int j = 0; j < continentFrequencyList.Count; j++)
            {
                pnoise += continentAmplitudeList[j] * noise.snoise(continentFrequencyList[j]*pf);
                ampSum += continentAmplitudeList[j];
            }
            pnoise /= ampSum;
            pnoise = math.saturate((pnoise + 1) * 0.5f);
            simplexValues[i] = pnoise;

            }
        // Now we can calculate what simplex value is below the sea. absolutely horribly on memory and time complexity. but I can't think of another
        // way to do it. TODO: maybe shaders can calculate it? Look into shader graph.
        float[] sortedSimps = new float[simplexValues.Length];
        Array.Copy(simplexValues, 0, sortedSimps, 0, simplexValues.Length);
        Array.Sort(sortedSimps);
        seaNoiseNumber = sortedSimps[(int)(sortedSimps.Length * seaLevel) - 1];
        // trying one for mountains too
        mountainNoiseNumber = sortedSimps[(int)(sortedSimps.Length * .9) - 1];
        approximateSeaRadius = radius * (1 + extremaScale * seaNoiseNumber);

        // then we colour in the points
        for (int i = 0; i < simplexValues.Length; i++)
        {
            float sp = simplexValues[i];
            // this'll scooch up the ocean floor/all the sub ocean numbers.
            float height = math.max(sp, seaNoiseNumber);
            // then switch to the height scale
            height = 1f + extremaScale * height;
            points[i] = points[i].normalized * radius * height;
            colours.Add(SimpleVertexColour(sp));
        }
    }

    /**
     * So the idea is that we first have to find the vertices that are visible, those will be the ones that get subdivided.
     *   i) If we take the plane that the camera can get a normal off the plane. 
     *  ii) Take the dot with a vertex x: 
     *          a) dot(x) >= 0 -> opposite direction camera is pointing (or on the horizon) i.e. 'visible face of planet.'
                b) dot(x) <  0 -> same direction camera is pointing i.e. 'opposing face of planet.'
     * iii) If we know the distance from the camera to the surface of planet (I'll start just by using |cam.pos|-radius)
     *      we can calculate how many of the vertices are in frame. So for each of vert x from previous step:
     *          a) 
     */
    public void GetVisibleVertices()
    {
        
    }

 

    /**
     * Just a simple way of adding 'water.' My thought is if we create another sphere (just a primitive)
     * with a radius that's (waterlevel)*(radius of planet) we can cover the planet in an overlapped level.
     * 
     */
    private void AddWaterSphere()
    {
        throw new NotImplementedException();
    }

    private float RidgeNoise(float3 point)
    {
        float r = math.saturate((noise.snoise(point) + 1) * 0.5f);
        return (float)(2 * (0.5-math.abs(0.5-r)));
    }


    /**
     * Updated to try and use a gradient. Apparently it's as simple as defining the gradient in the editor (in this case because it's a field).
     * Then calling evaulate in that spot. I have to evaluate based [0,1]   
     * https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Gradient.html
     * 
     * @param pnoise the generated pnoise value.
     */
    private Color SimpleVertexColour(float noise)
    {   

        if (noise <= seaNoiseNumber / 1.8) return deepWater;
        if (noise <= seaNoiseNumber) return otherWater;

        float bounded = math.unlerp(0, 1, noise);
        return terrainGradient.Evaluate(bounded);

        //// magic number in process of figuring out what height looks good for snow.
        //else if (noise < seaNoiseNumber + .02) return Color.sandyBrown;
        //else if (noise < seaNoiseNumber + .05) return Color.paleGoldenRod;
        //else if (noise < seaNoiseNumber + .253) return Color.darkGreen;
        //else if (noise < seaNoiseNumber * .76) return Color.dimGray;
        //else return Color.snow;


        

    }

    /**
     * Stereographic projection of 3D points onto a 2D plane. Will at some point allow you to specify a different unit sphere radius,
     * but I don't know if thats even necessary.
     * This method is not implemented yet.
     *
     * @param cgalPoints The array of 3D points to project.
     * @return An array of 2D points resulting from the projection.
     */
    private Vector2[] StereographicProject2D(Vector3[] points3d)
    {
        Vector2[] planePoints = new Vector2[points3d.Length];
        for (int i = 0; i < points3d.Length; i++)
        {
            Vector3 p = points3d[i];

            double x = p.x / (1 - p.z);
            double y = p.y / (1 - p.z);

            planePoints[i] = new Vector2((float)x, (float)y);

        }
        return planePoints;
    }

    /**
     * Stereographic projection of 2D points onto a 3D unit sphere. Will at some point allow you to specify a different unit sphere radius,
     * but I don't know if thats even necessary.
     * This method is not implemented yet.
     *
     * @param points2d The array of 2D points to project.
     * @return An array of 3D points resulting from the projection.
     */
    private Vector3[] StereographicProject3D(Vector2[] points2d)
    {
        Vector3[] spherePoints = new Vector3[points2d.Length];

        double px;
        double py;

        double xySquared;
        for (int i = 0; i < points2d.Length; i++)
        {
            px = points2d[i].x;
            py = points2d[i].y;
            xySquared = math.square(px) + math.square(py);

            double x = 2 * px / (1 + xySquared);
            double y = 2 * py / (1 + xySquared);
            double z = (-1 + xySquared) / (1 + xySquared);

            spherePoints[i] = new Vector3((float)x, (float)y, (float)z);
        }
        return spherePoints;
    }

    // Update is called once per frame
    void Update()
    {
        gameObject.transform.Rotate(Vector3.back, 3 * Time.deltaTime);
    }

    // https://www.redblobgames.com/x/1842-delaunay-voronoi-sphere/
    // paper its based on https://arxiv.org/pdf/0912.4540
    // seems easiser that the subdivided icosahedron
    private List<Vector3> FibbSphere(int numPoints)
    {

        //CGALDotNetGeometry.Numerics.Point3d[numPoints] pointArray = new Vector3[numPoints];
        List<Vector3> pointArray = new List<Vector3>(numPoints);
        // yoinked, why do we need this? it's apparently the golden angle
        double phi = math.PI_DBL * (math.sqrt(5.0) - 1.0);

        for ( int i = 0; i < numPoints; i++)
        {
            // y coord
            double y = 1 - ((double)i / (numPoints - 1)) * 2;
            // radius from the center at this point.
            double radius = math.sqrt(1 - y * y);

            double theta = phi * i;

            double x = math.cos(theta) * radius;    
            double z = math.sin(theta) * radius;


            pointArray.Add(new Vector3((float)x, (float)y, (float)z));

        }
        return pointArray;
    }
}
